﻿using Azure.Identity;
using AIShell.Abstraction;
using System.Diagnostics;

namespace AIShell.Azure.PowerShell;

public sealed class AzPSAgent : ILLMAgent
{
    public string Name => "az-ps";
    public string Description => "This AI assistant can help generate Azure PowerShell scripts or commands for managing Azure resources and end-to-end scenarios that involve multiple different Azure resources.";
    public string Company => "Microsoft";
    public List<string> SampleQueries => [
        "Create a VM with a public IP address",
        "How to stop all VMs with the port 22 opened?",
        "Create a container app using docker image nginx"
    ];
    public Dictionary<string, string> LegalLinks { private set; get; } = null;
    public string SettingFile { private set; get; } = null;

    private const string SettingFileName = "az-ps.agent.json";

    private string _configRoot;
    private RenderingStyle _renderingStyle;
    private AzPSChatService _chatService;
    private MetricHelper _metricHelper;

    public void Dispose()
    {
        _chatService.Dispose();
    }

    public void Initialize(AgentConfig config)
    {
        _renderingStyle = config.RenderingStyle;
        _configRoot = config.ConfigurationRoot;
        SettingFile = Path.Combine(_configRoot, SettingFileName);

        string tenantId = null;
        if (config.Context is not null)
        {
            config.Context?.TryGetValue("tenant", out tenantId);
        }

        LegalLinks = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Terms"] = "https://aka.ms/TermsofUseCopilot",
            ["Privacy"] = "https://aka.ms/privacy",
            ["FAQ"] = "https://aka.ms/CopilotforAzureClientToolsFAQ",
            ["Transparency"] = "https://aka.ms/CopilotAzCLIPSTransparency",
        };

        _metricHelper = new MetricHelper(AzPSChatService.Endpoint);
        _chatService = new AzPSChatService(config.IsInteractive, tenantId);
    }

    public IEnumerable<CommandBase> GetCommands() => null;

    public bool CanAcceptFeedback(UserAction action) => !MetricHelper.TelemetryOptOut;

    public void OnUserAction(UserActionPayload actionPayload)
    {
        // DisLike Action
        string DetailedMessage = null;
        if (actionPayload.Action == UserAction.Dislike)
        {
            DislikePayload dislikePayload = (DislikePayload)actionPayload;
            DetailedMessage = string.Format("{0} | {1}", dislikePayload.ShortFeedback, dislikePayload.LongFeedback);
        }

        // TODO: Extract into RecrodActionTelemetry : RecordTelemetry()
        _metricHelper.LogTelemetry(
            new AzTrace()
            {
                Command = actionPayload.Action.ToString(),
                CorrelationID = _chatService.CorrelationID,
                EventType = "Feedback",
                Handler = "Azure PowerShell",
                DetailedMessage = DetailedMessage
            });
    }

    public Task RefreshChatAsync(IShell shell, bool force)
    {
        // Reset the history so the subsequent chat can start fresh.
        _chatService.ChatHistory.Clear();

        return Task.CompletedTask;
    }

    public async Task<bool> ChatAsync(string input, IShell shell)
    {
        IHost host = shell.Host;
        CancellationToken token = shell.CancellationToken;

        try
        {
            // The AzPS endpoint can return status information in the streaming manner, so we can
            // update the status message while waiting for the answer payload to come back.
            using ChunkReader chunkReader = await host.RunWithSpinnerAsync(
                status: "Thinking ...",
                func: async context => await _chatService.GetStreamingChatResponseAsync(context, input, token)
            ).ConfigureAwait(false);

            if (chunkReader is null)
            {
                // Operation was cancelled by user.
                return true;
            }

            using var streamingRender = host.NewStreamRender(token);

            try
            {
                while (true)
                {
                    ChunkData chunk = await chunkReader.ReadChunkAsync(token).ConfigureAwait(false);
                    if (chunk is null || chunk.Status.Equals("Finished Generate Answer", StringComparison.Ordinal))
                    {
                        break;
                    }

                    streamingRender.Refresh(chunk.Message);
                }
            }
            catch (OperationCanceledException)
            {
                // Operation was cancelled by user.
            }

            string accumulatedContent = streamingRender.AccumulatedContent;
            _chatService.AddResponseToHistory(accumulatedContent);

            if (!MetricHelper.TelemetryOptOut)
            {
                // TODO: extract into RecordQuestionTelemetry() : RecordTelemetry()

                _metricHelper.LogTelemetry(
                    new AzTrace()
                    {
                        CorrelationID = _chatService.CorrelationID,
                        EventType = "Chat",
                        Handler = "Azure PowerShell"
                    });
            }
        }
        catch (RefreshTokenException ex)
        {
            Exception inner = ex.InnerException;
            if (inner is CredentialUnavailableException)
            {
                host.WriteErrorLine($"Access token not available. Query cannot be served.");
                host.WriteErrorLine($"The '{Name}' agent depends on the Azure PowerShell credential to acquire access token. Please run 'Connect-AzAccount' from a command-line shell to setup account.");
            }
            else
            {
                host.WriteErrorLine($"Failed to get the access token. {inner.Message}");
            }

            return false;
        }
        finally
        {

        }

        return true;
    }
}

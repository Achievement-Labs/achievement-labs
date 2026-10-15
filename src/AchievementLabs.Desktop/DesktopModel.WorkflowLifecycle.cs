using System.ComponentModel;

namespace AchievementLabs.Desktop;

public sealed partial class DesktopModel
{
    private bool WorkflowBusy => legacyBridge?.IsBusy == true || statsEditor?.IsBusy == true
        || statsEditor?.UpdateAllStatsCommand.IsRunning == true || win8Tools?.IsRuntimeSubmitting == true
        || steamQueue?.IsBusy == true || xboxBuildQueue?.IsAdding == true || xboxQueue?.IsRunning == true;

    private T ObserveWorkflow<T>(T workflow) where T : INotifyPropertyChanged
    {
        workflow.PropertyChanged += WorkflowChanged;
        return workflow;
    }

    private void WorkflowChanged(object? sender, PropertyChangedEventArgs args) { Changed(nameof(CanDisconnect)); Changed(nameof(CanStartPresence)); }

    private void ResetWorkflowAccount()
    {
        completionScanCancellation?.Cancel(); completionProgress.Clear();
        queueAccount.XAUTH = "";
        queueAccount.XUIDOnly = "";
        queueAccount.EventsToken = "";
        queueAccount.LastOAuthResponse = null;
        xboxBuildQueue?.Refresh();
        LegacyXuid = "";
        lookupXuid = "";
        LookupOutput = "";
        StatsOutput = "";
        if (win8Tools != null) win8Tools.Xuid = "";
        if (legacyBridge != null)
        {
            legacyBridge.AuthorizationHeader = "";
            legacyBridge.MsaAccessToken = "";
            legacyBridge.Xuid = "";
            legacyBridge.LastResponseDetails = "";
            legacyBridge.UnlockLog.Clear();
            legacyBridge.OnNavigatedTo();
        }
        if (statsEditor != null) { statsEditor.ReleaseClient(); statsEditor.IsInitialized = false; statsEditor.StatItems.Clear(); }
        if (xboxQueue != null) { xboxQueue.ReleaseClient(); xboxQueue.IsInitialized = false; }
    }
}

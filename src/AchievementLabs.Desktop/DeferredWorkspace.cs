using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace AchievementLabs.Desktop;

public sealed class DeferredWorkspace : ContentControl
{
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<DeferredWorkspace, bool>(nameof(IsActive));

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public event EventHandler? WorkspaceCreated;

    public IDataTemplate? WorkspaceTemplate { get; set; }

    protected override Type StyleKeyOverride => typeof(ContentControl);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsActiveProperty && IsActive && Content is null &&
            WorkspaceTemplate?.Build(DataContext) is Control workspace)
        {
            AchievementLabs.MultiSelect.SearchClearButtons.Attach(workspace);
            Content = workspace;
            WorkspaceCreated?.Invoke(this, EventArgs.Empty);
        }
    }
}

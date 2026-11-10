namespace AchievementLabs.Desktop;

public sealed record UbisoftSpoolItem(string Name, string RelativePath, string SourcePath);

public sealed partial class DesktopModel
{
    private UbisoftSpoolItem[] ubisoftSpools = [];
    private UbisoftSpoolItem? selectedUbisoftSpool;
    private string ubisoftSpoolTarget = DefaultUbisoftSpoolTarget();
    private static string SavedSpoolLibrary()
    {
        if(OfflineChecks)return "";
        try {var path=AchievementLabs.Core.AchievementLabsPaths.LocalFile("ubisoft-spool-library.txt");return File.Exists(path)?File.ReadAllText(path).Trim():"";}
        catch(Exception error) when(error is IOException or UnauthorizedAccessException){return "";}
    }
    private string ubisoftSpoolSourceDirectory = SavedSpoolLibrary();
    public string UbisoftSpoolSourceDirectory
    {
        get=>ubisoftSpoolSourceDirectory;
        set
        {
            ubisoftSpoolSourceDirectory=value??"";Changed();LoadUbisoftTools();
            if(!OfflineChecks)
                try {var path=AchievementLabs.Core.AchievementLabsPaths.LocalFile("ubisoft-spool-library.txt");Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,ubisoftSpoolSourceDirectory);}
                catch(Exception error) when(error is IOException or UnauthorizedAccessException){UbisoftToolStatus+=" Source folder could not be remembered.";}
        }
    }
    private string ubisoftOverlayTarget = "";
    private string ubisoftOverlayArchitecture = "x64";
    private string ubisoftToolStatus = "Choose a spool or an Ubisoft game folder.";

    public UbisoftSpoolItem[] UbisoftSpools { get => ubisoftSpools; private set { ubisoftSpools = value; Changed(); } }
    public UbisoftSpoolItem? SelectedUbisoftSpool { get => selectedUbisoftSpool; set { selectedUbisoftSpool = value; Changed(); } }
    public string UbisoftSpoolTarget { get => ubisoftSpoolTarget; set { ubisoftSpoolTarget = value ?? ""; Changed(); } }
    public string UbisoftOverlayTarget { get => ubisoftOverlayTarget; set { ubisoftOverlayTarget = value ?? ""; Changed(); } }
    public string UbisoftOverlayArchitecture { get => ubisoftOverlayArchitecture; set { ubisoftOverlayArchitecture = value ?? "x64"; Changed(); } }
    public string[] UbisoftOverlayArchitectures { get; } = ["x64", "x86"];
    public string UbisoftToolStatus { get => ubisoftToolStatus; private set { ubisoftToolStatus = value; Changed(); } }

    public void LoadUbisoftTools()
    {
        var root = UbisoftSpoolSourceDirectory;
        try
        {
            UbisoftSpools = Directory.Exists(root)
                ? Directory.EnumerateFiles(root,"*.spool",SearchOption.AllDirectories).Take(10000)
                    .Select(path=>new UbisoftSpoolItem(Path.GetFileNameWithoutExtension(path),Path.GetRelativePath(root,path),path))
                    .OrderBy(item=>item.RelativePath,StringComparer.OrdinalIgnoreCase).ToArray()
                : [];
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException)
        { UbisoftSpools=[];SelectedUbisoftSpool=null;UbisoftToolStatus="The local spool library could not be read. Choose an accessible folder.";return; }
        SelectedUbisoftSpool = UbisoftSpools.FirstOrDefault();
        UbisoftToolStatus = UbisoftSpools.Length>0 ? $"{UbisoftSpools.Length} external spool files available." : "Choose your local spool library. Spool packs are no longer bundled.";
    }

    public void InstallSelectedUbisoftSpool()
    {
        try
        {
            var item = SelectedUbisoftSpool ?? throw new InvalidOperationException("Choose a spool file.");
            if (string.IsNullOrWhiteSpace(UbisoftSpoolTarget)) throw new DirectoryNotFoundException();
            Directory.CreateDirectory(UbisoftSpoolTarget);
            var destination = Path.Combine(UbisoftSpoolTarget, Path.GetFileName(item.SourcePath));
            BackupIfPresent(destination);
            File.Copy(item.SourcePath, destination, true);
            UbisoftToolStatus = $"Installed {Path.GetFileName(destination)} to {UbisoftSpoolTarget}.";
        }
        catch (Exception ex) { UbisoftToolStatus = "Spool installation failed: " + ex.Message; }
    }

    public void InstallUbisoftOverlay()
    {
        try
        {
            if (!Directory.Exists(UbisoftOverlayTarget)) throw new DirectoryNotFoundException("Choose the Ubisoft game overlay folder.");
            var x64 = string.Equals(UbisoftOverlayArchitecture, "x64", StringComparison.OrdinalIgnoreCase);
            var source = Path.Combine(AppContext.BaseDirectory, "uplayachievements", x64 ? "UplayAchievementsTool_x64_1.0.0" : "UplayAchievementsTool_x86_1.0.0", "overlay.dll");
            if (!File.Exists(source)) throw new FileNotFoundException("Packaged Ubisoft overlay tool is missing.", source);
            var targetName = x64 ? "overlay64.dll" : "overlay.dll";
            var backupName = x64 ? "UbiOverlay64.dll" : "UbiOverlay.dll";
            var destination = Path.Combine(UbisoftOverlayTarget, targetName);
            var backup = Path.Combine(UbisoftOverlayTarget, backupName);
            if (File.Exists(destination) && !File.Exists(backup)) File.Copy(destination, backup, false);
            File.Copy(source, destination, true);
            UbisoftToolStatus = $"Installed the {UbisoftOverlayArchitecture} achievement overlay. Original retained as {backupName}.";
        }
        catch (Exception ex) { UbisoftToolStatus = "Overlay installation failed: " + ex.Message; }
    }

    private static void BackupIfPresent(string path)
    {
        if (!File.Exists(path)) return;
        var backup = path + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".bak";
        File.Copy(path, backup, false);
    }

    private static string DefaultUbisoftSpoolTarget()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        return Path.Combine(programFiles, "Ubisoft", "Ubisoft Game Launcher", "cache", "achievements", "spool");
    }
}

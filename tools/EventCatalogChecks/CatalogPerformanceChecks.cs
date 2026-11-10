using System.Diagnostics;
using AchievementLabs.Core;

static class CatalogPerformanceChecks
{
    public static void Run()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var allocated = GC.GetTotalAllocatedBytes(true);
        var timer = Stopwatch.StartNew();
        using var catalog = new EventCatalogClient(true, officialCatalog: true);
        var support = catalog.GetLibrarySupport();
        timer.Stop();
        Console.WriteLine($"support: titles={support.Count}, elapsedMs={timer.ElapsedMilliseconds}, allocatedBytes={GC.GetTotalAllocatedBytes(true)-allocated}, peakWorkingSetBytes={Process.GetCurrentProcess().PeakWorkingSet64}");
        allocated = GC.GetTotalAllocatedBytes(true); timer.Restart();
        for (int i=0;i<100;i++) PublicCatalogClient.BundledSearch("Halo");
        timer.Stop();
        Console.WriteLine($"100 local searches: elapsedMs={timer.ElapsedMilliseconds}, allocatedBytes={GC.GetTotalAllocatedBytes(true)-allocated}");
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Console.WriteLine($"after collection: managedBytes={GC.GetTotalMemory(false)}, workingSetBytes={Process.GetCurrentProcess().WorkingSet64}");
    }
}

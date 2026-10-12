using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace AchievementLabs.Services.Gfwl;

public sealed record GfwlAchievement(int Id, string Name, string Description, int Gamerscore, bool Unlocked, bool UnlockedOnline, DateTime? UnlockedAt)
{
    public string State => !Unlocked ? "Unachieved" : UnlockedOnline ? "Achieved online" : "Achieved offline";
    public string TimeText => UnlockedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? State;
}

// Wire format recovered from GFWLAM 1.1 and its engine.dll. The engine polls this
// named mapping every 100 ms after being loaded into a signed-in 32-bit GFWL game.
public sealed class GfwlAchievementSession : IDisposable
{
    private const string MappingName = "gfjhakaalsfdggdfasd";
    private const long Capacity = 5_000_000;
    private const int RowSize = 0x1818;
    private const int RowStart = 8;
    private const int MaximumRows = 800;
    private readonly MemoryMappedFile mapping;
    private readonly MemoryMappedViewAccessor view;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;
    public int ProcessId { get; }

    public GfwlAchievementSession(int processId) : this(processId, MappingName) { }

    public GfwlAchievementSession(int processId, string mappingName)
    {
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        ProcessId = processId;
        try { mapping = MemoryMappedFile.CreateNew(mappingName, Capacity, MemoryMappedFileAccess.ReadWrite); }
        catch (IOException error) { throw new IOException("A GFWLAM session is already using the shared achievement channel. Close it before connecting Achievement Labs.", error); }
        view = mapping.CreateViewAccessor(0, Capacity, MemoryMappedFileAccess.ReadWrite);
        view.Write(0, 0);
    }

    public async Task<IReadOnlyList<GfwlAchievement>> ReadAchievementsAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try { return await ReadCoreAsync(cancellationToken); }
        finally { gate.Release(); }
    }

    public async Task<GfwlAchievement> EarnAsync(int achievementId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await ReadCoreAsync(cancellationToken);
            var target = current.FirstOrDefault(item => item.Id == achievementId) ?? throw new InvalidDataException("That achievement is no longer in the selected game's catalog.");
            if (target.Unlocked) throw new InvalidOperationException("This achievement is already earned.");
            view.Write(4, achievementId);
            view.Write(0, 0x1001);
            int response;
            try { response = await AwaitResponseAsync([0x2002, 0x2003, 0x3000], cancellationToken); }
            finally { if (!disposed) view.Write(0, 0); }
            if (response == 0x3000) throw new InvalidOperationException("The GFWL game has no signed-in profile.");
            if (response != 0x2002) throw new InvalidOperationException("The GFWL game rejected the achievement write.");
            var refreshed = await ReadCoreAsync(cancellationToken);
            var verified = refreshed.FirstOrDefault(item => item.Id == achievementId);
            if (verified?.Unlocked != true) throw new InvalidOperationException("The game reported a write, but the achievement was not verified as earned. Refresh the list before retrying.");
            return verified;
        }
        finally { gate.Release(); }
    }

    private async Task<IReadOnlyList<GfwlAchievement>> ReadCoreAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        view.Write(0, 0x1000);
        try
        {
            var response = await AwaitResponseAsync([0x2000, 0x2001, 0x3000], cancellationToken);
            if (response == 0x3000) throw new InvalidOperationException("The GFWL game has no signed-in profile.");
            if (response != 0x2000) throw new InvalidOperationException("The GFWL game could not return its achievement list.");
            var count = view.ReadInt32(4);
            if (count < 0 || count > MaximumRows || RowStart + (long)count * RowSize > Capacity)
                throw new InvalidDataException("The GFWL engine returned an invalid achievement count.");
            var rows = new List<GfwlAchievement>(count);
            for (var index = 0; index < count; index++)
            {
                var row = RowStart + index * RowSize;
                var id = view.ReadInt32(row);
                var name = ReadText(row + 4);
                var description = ReadText(row + 0x804);
                var score = view.ReadInt32(row + 0x1808);
                var flags = view.ReadInt32(row + 0x1814);
                var unlocked = (flags & 0x20000) != 0;
                var online = unlocked && (flags & 0x10000) != 0;
                DateTime? time = null;
                if (online)
                {
                    try { time = DateTime.FromFileTimeUtc(view.ReadInt64(row + 0x180c)); }
                    catch (ArgumentOutOfRangeException) { }
                }
                rows.Add(new GfwlAchievement(id, name, description, score, unlocked, online, time));
            }
            return rows;
        }
        finally { if (!disposed) view.Write(0, 0); }
    }

    private string ReadText(long offset)
    {
        var bytes = new byte[0x800];
        view.ReadArray(offset, bytes, 0, bytes.Length);
        var length = 0;
        while (length + 1 < bytes.Length && (bytes[length] != 0 || bytes[length + 1] != 0)) length += 2;
        return Encoding.Unicode.GetString(bytes, 0, length);
    }

    private async Task<int> AwaitResponseAsync(int[] expected, CancellationToken cancellationToken)
    {
        var stopAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
        while (Stopwatch.GetTimestamp() < stopAt)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            try { using var process = Process.GetProcessById(ProcessId); if (process.HasExited) throw new InvalidOperationException("The selected GFWL game closed."); }
            catch (ArgumentException) { throw new InvalidOperationException("The selected GFWL game closed."); }
            var value = view.ReadInt32(0);
            if (expected.Contains(value)) return value;
            await Task.Delay(50, cancellationToken);
        }
        view.Write(0, 0);
        throw new TimeoutException("The GFWL game did not respond within five seconds.");
    }

    private void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException(nameof(GfwlAchievementSession)); }
    public void Dispose() { if (disposed) return; disposed = true; view.Dispose(); mapping.Dispose(); gate.Dispose(); }
}

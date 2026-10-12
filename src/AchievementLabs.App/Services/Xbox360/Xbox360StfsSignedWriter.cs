namespace AchievementLabs.Services.Xbox360;

/// <summary>Writes modified GPDs to a new local CON package and verifies its signature.</summary>
public sealed class Xbox360StfsSignedWriter(Xbox360StfsUnsignedWriter unsignedWriter)
{
    public void Write(string source, string destination, IReadOnlyDictionary<string, byte[]> replacements)
    {
        unsignedWriter.Write(source, destination, replacements);
        try { Xbox360ConSigner.SignFile(destination); }
        catch
        {
            File.Delete(destination);
            throw;
        }
    }
}

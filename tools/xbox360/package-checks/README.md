# Xbox 360 package reader regression checks

Run from the repository root:

```powershell
dotnet run --project tools/xbox360/package-checks/PackageChecks.csproj
```

Exercises the actual package reader against synthetic CON packages with chained file tables at 3, 170 and 171 allocated blocks, with each active hash-table copy. Verifies entry extraction and rejection of corrupted tables. The original reader reproduces issue #63's hash-table verification error on the small-profile fixture.

No account, signing material, real profiles, network requests or unlocks are used.

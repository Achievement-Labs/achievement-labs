# README visuals

`banner.svg` is a text-based banner using the dark and mint palette of the app. Keep the organization, desktop, and mobile banners consistent when updating the presentation.

The PNGs are unmodified output from the desktop application's explicit offline visual-preview mode, built from commit `2832322120179c77c62db512ef89aaf912af4160`:

```powershell
dotnet build src/AchievementLabs.Desktop/AchievementLabs.Desktop.csproj -c Release
.\src\AchievementLabs.Desktop\bin\Release\net9.0-windows10.0.19041.0\AchievementLabs.exe --visual-preview --preview-output <temporary-output-directory>
```

- `desktop-session.png`: `combined-large.png`
- `desktop-library.png`: `library-wide.png`
- `desktop-home.png`: `home-dashboard.png`

Use synthetic data and the preview profile. Inspect replacement screenshots before committing them, retain the sample-data caption, and never capture account tokens, pairing codes, private profiles, or live account exports. These images demonstrate the interface, not live achievement credit.

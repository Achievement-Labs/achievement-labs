using AchievementLabs.MultiSelect;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using System.ComponentModel;

static class Test
{
 static void Assert(bool v,string message) { if(!v)throw new Exception(message); }
 static void Click(Button b) { b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs(); }
 static IEnumerable<T> Desc<T>(Control c)=>c.GetLogicalDescendants().OfType<T>();
 static Window Open(FakeWindow w) { Click(Desc<Button>(w).Single(b=>(b.Content as string)=="Select multiple…"));return w.OwnedWindows.Single(); }
 static void SelectAll(Window d)=>Click(Desc<Button>(d).Single(b=>(b.Content as string)=="Select all eligible"));
 static void Submit(Window d)=>Click(Desc<Button>(d).Single(b=>(b.Content as string)=="Unlock / retry selected"));
 public static void Main(string[] args)
 {
  if(args.Contains("--startup-mode"))
  {
   var configure=typeof(AchievementLabs.Desktop.MainWindow).Assembly.GetType("AchievementLabs.Desktop.Program")!.GetMethod("ConfigureStartup",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
   foreach(var launch in new[]{Array.Empty<string>(),new[]{"--xbox360-local-test"},new[]{"--local-360-smoke-test"}})
   {
    AppContext.SetSwitch("AchievementLabs.OfflineChecks",false);
    AppContext.SetSwitch("AchievementLabs.StartOnXbox360",false);
    configure.Invoke(null,new object[]{launch});
    Assert((AppContext.TryGetSwitch("AchievementLabs.OfflineChecks",out var isolated)&&isolated)==(launch.Length>0),"Only explicit preview flags disable Xbox attachment");
    Assert((AppContext.TryGetSwitch("AchievementLabs.StartOnXbox360",out var start360)&&start360)==(launch.Length>0),"Normal EXE startup does not force the 360 page");
   }
   Console.WriteLine("PASS: ordinary startup enables normal Xbox attachment; only explicit preview/test arguments isolate the 360 page.");return;
  }
  AppContext.SetSwitch("AchievementLabs.OfflineChecks", true);
  if(args.Length==4 && args[0]=="--verify-mobile360"){Mobile360Checks.Run(args[1],args[2],args[3]);return;}
  if(args.Length==2 && args[0]=="--description-layout"){AchievementDescriptionLayoutChecks.Run(args[1]);return;}
  if(args.Contains("--queue-descriptions")){SavedGameQueueTests.Run();return;}
  if(args.Length is 2 or 3 && args[0]=="--xbox360-layout"){Xbox360LayoutChecks.Run(args[1],args.Length==3?args[2]:null);return;}
  if(args.Length==3 && args[0]=="--prepare360-signing"){AchievementLabs.Services.Xbox360.Xbox360SigningSession.LoadHorizonFile(args[1]);AchievementLabs.Services.Xbox360.Xbox360SigningSession.WriteLocalBuildIdentity(args[2]);AchievementLabs.Services.Xbox360.Xbox360SigningSession.Clear();Console.WriteLine("Prepared private local-build signing input. No key values printed.");return;}
  if(args.Length==4 && args[0]=="--xbox360-signing-check"){Xbox360SigningChecks.Run(args[1],args[2],args[3]);return;}
  if(args.Length==3 && args[0]=="--xbox360-profile-check"){Xbox360ProfilePreviewChecks.Run(args[1],args[2]);return;}
  if(args.Contains("--exophase-360")){Exophase360Tests.Run();return;}
  if(args.Length==2 && args[0]=="--exophase-live"){using var exo=new AchievementLabs.Services.Xbox360.Exophase360Client();try{var games=exo.FindGamesAsync(args[1],false).GetAwaiter().GetResult();Console.WriteLine($"Live public Exophase lookup: {games.Count} Xbox 360 games.");}catch(Exception e){Console.WriteLine("Live lookup unavailable: "+e.Message);Environment.ExitCode=2;}return;}
  if(args.Contains("--xbox360-preview")){Xbox360TimestampTests.Run();return;}
  if(args.Contains("--title-search")){TitleSearchTests.Run();return;}
  if(args.Contains("--library-refresh")){LibraryRefreshTests.Run();return;}
  if(args.Contains("--token-validation")){EventTokenRegressionTests.Run();return;}
  if(args.Contains("--ui-tweaks")){UITweakTests.Run();return;}
  if(args.Contains("--queue-tools")){DlcCatalogueTests.Run();CompletionPresentationTests.Run();GameDetailsTests.Run();QueueToolTests.Run();SavedGameQueueTests.Run();
  QueueTransferTests.Run();return;}
  if(args.Contains("--playtime")){PlaytimeTests.Run();return;}
  if(args.Contains("--export")){ExportTests.Run();return;}
  if(args.Contains("--mapping")){MappingTests.Run();return;}
  if(args.Contains("--editions")){LocalCatalogueIntegrationTests.CheckUnapprovedMappings();return;}
  if(args.Contains("--port")){PortTests.Run();return;}
  if(args.Contains("--integration") || args.Contains("--scroll"))
  {
   AppContext.SetSwitch("AchievementLabs.OfflineChecks", true);
   string testRoot=Environment.GetEnvironmentVariable("AL_TEST_ROOT")??AppContext.BaseDirectory;
   string root=System.IO.Path.GetFullPath(testRoot);
   System.Runtime.Loader.AssemblyLoadContext.Default.Resolving+=(_,name)=>
   {
    string path=System.IO.Path.Combine(root,name.Name+".dll");
    return System.IO.File.Exists(path)?System.Reflection.Assembly.LoadFrom(path):null;
   };
   System.Reflection.Assembly.LoadFrom(System.IO.Path.GetFullPath(testRoot+"/AchievementLabs.Core.dll"));
   var assembly=System.Reflection.Assembly.LoadFrom(System.IO.Path.GetFullPath(testRoot+"/AchievementLabs.dll"));
   var appType=assembly.GetTypes().Single(t=>t.Name=="App" && typeof(Application).IsAssignableFrom(t));
   AppBuilder.Configure(()=> (Application)Activator.CreateInstance(appType)!).UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
   var window=(Window)Activator.CreateInstance(assembly.GetTypes().Single(t=>t.Name=="MainWindow"))!;
   var saveAll = Desc<Button>(window).Single(b=>b.Name=="SaveAllSettings");
   Assert(Desc<Button>(window).Any(b=>b.Name=="UnlockXbox360Selected" && !b.IsEnabled),"360 unlock is one explicit action and starts gated");
   Assert(!Desc<Button>(window).Any(b=>b.Name is "StageXbox360Review" or "SaveXbox360WorkingCopy"),"Separate stage/save actions removed from normal 360 workflow");
   Assert(Desc<Button>(window).Any(b=>b.Name=="SetupXbox360Signing"),"Custom signing setup remains accessible");
   Assert(!Desc<CheckBox>(window).Any(c=>Equals(c.Content,"This is my profile") || Equals(c.Content,"The loaded profile is mine and I have a backup")),"Manual ownership acknowledgement removed");
   Assert(!Desc<TextBlock>(window).Any(t=>t.Text=="RECENTLY PLAYED"),"360 recent-game cards removed");
   Assert(Desc<TextBox>(window).Any(b=>b.Name=="Xbox360ExophaseName") && Desc<Button>(window).Any(b=>b.Name=="LookupXbox360ExophaseButton"),"Exophase name lookup controls are attached");
   Assert(Desc<ComboBox>(window).Any(c=>c.Name=="Xbox360AchievementFilter") && Desc<ComboBox>(window).Any(c=>c.Name=="Xbox360AchievementSort"),"360 filter and sort controls are bound in the real window");
   Assert(!Desc<TextBlock>(window).Any(t=>t.Text=="Settings, saved queues & tools"),"Redundant queue heading removed");
   var platformFilters = Desc<ItemsControl>(window).Single(c=>c.Name=="TitleSearchPlatformFilters");
   Assert(platformFilters.Items.Count==9,"Nine title-search platforms available");
   Click(Desc<Button>(window).Single(b=>b.Name=="TitlePlatformsNone"));
   Assert(platformFilters.Items.Cast<AchievementLabs.Desktop.TitlePlatformFilter>().All(p=>!p.Selected),"None clears all platform toggles");
   Click(Desc<Button>(window).Single(b=>b.Name=="TitlePlatformsAll"));
   Assert(platformFilters.Items.Cast<AchievementLabs.Desktop.TitlePlatformFilter>().All(p=>p.Selected),"All restores platform toggles");
   Assert(saveAll.Parent is Grid settingsPanel && Grid.GetRow(saveAll)==0 && settingsPanel.Children.OfType<TabControl>().Any(t=>Grid.GetRow(t)==1),"Save all settings remains above the category tabs");
   Assert(Desc<CheckBox>(window).Any(c=>Equals(c.Content,"Remember window size and position")),"Window size setting present");
   Assert(Desc<CheckBox>(window).Any(c=>Equals(c.Content,"Always open maximized")),"Maximize setting present");
   Assert(Desc<TextBox>(window).Count(c=>c.Tag as string=="build-xbox-queue")==2,"Enter queue building attached to both configuration fields only");
   Assert(Desc<TextBox>(window).Count(c=>c.Tag as string=="lookup-spoof")==1,"Only title lookup accepts Enter, never start presence");
   Assert(!Desc<TextBox>(window).Any(c=>c.AcceptsReturn && c.Tag is string tag && tag=="save-token"),"Multiline text keeps normal Enter behaviour");
   var settingsTabs=Desc<TabControl>(window).Single(t=>t.Name=="DesktopSettingsTabs");
   Assert(settingsTabs.Items.OfType<TabItem>().Select(t=>t.Header?.ToString()).SequenceEqual(new[]{"Account","Sync","Library","App","Debug","Appearance"}),"Desktop settings categories are explicit and complete");
   Assert(Desc<Button>(window).Count(b=>b.Name=="SaveAllSettings")==1,"One global save remains accessible above settings tabs");
   using (var routeClient = new AchievementLabs.Core.EventCatalogClient(true)) {
       Assert(!routeClient.BundledTitleIds.Contains("345903534"),"Legacy Minecraft uses upstream route rather than Bedrock bundled data");
       Assert(routeClient.BundledTitleIds.Contains("1828326430"),"Other Minecraft editions retain bundled route");
   }
   {
       var transfer = AchievementLabs.Core.MobileTransfer.Create("XBL3.0 x=123;synthetic","42","synthetic-event",null);
       var qrFrames = AchievementLabs.Core.MobileTransfer.QrFrames(transfer);
       Assert(qrFrames.Length>0 && qrFrames.All(f=>f.StartsWith("ALPC1|") && f.Length<1000),"QR transfer frames are bounded");
       var qrBytes=Convert.FromBase64String(string.Concat(qrFrames.Select(f=>f.Split('|')[4])));
       using var qrPayload=System.Text.Json.JsonDocument.Parse(qrBytes);
       Assert(qrPayload.RootElement.GetProperty("key").GetString()==transfer.PairingKey,"QR pairing key retained");
       Assert(Convert.FromBase64String(qrPayload.RootElement.GetProperty("file").GetString()!).SequenceEqual(transfer.Bytes),"QR encrypted payload retained");
       Assert(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(qrBytes)).ToLowerInvariant()==qrFrames[0].Split('|')[1],"QR integrity digest matches payload");
       using var doc = System.Text.Json.JsonDocument.Parse(transfer.Bytes);
       var transferRoot = doc.RootElement;
       var keyText = transfer.PairingKey.Replace('-','+').Replace('_','/'); keyText += new string('=',(4-keyText.Length%4)%4);
       var key = Convert.FromBase64String(keyText); var ciphertext = Convert.FromBase64String(transferRoot.GetProperty("ciphertext").GetString()!); var plain = new byte[ciphertext.Length];
       using var aes = new System.Security.Cryptography.AesGcm(key,16);
       aes.Decrypt(Convert.FromBase64String(transferRoot.GetProperty("nonce").GetString()!),ciphertext,Convert.FromBase64String(transferRoot.GetProperty("tag").GetString()!),plain,System.Text.Encoding.UTF8.GetBytes("AchievementLabs-PC-Transfer-v1"));
       using var payload = System.Text.Json.JsonDocument.Parse(plain);
       Assert(payload.RootElement.GetProperty("xuid").GetString()=="42","Mobile transfer retains the explicitly connected account");
       Assert(payload.RootElement.GetProperty("eventToken").GetString()=="synthetic-event","Mobile transfer retains the current event token");
       Assert(!System.Text.Encoding.UTF8.GetString(transfer.Bytes).Contains("synthetic-event"),"Mobile file is encrypted");
       Assert(!payload.RootElement.TryGetProperty("refreshToken",out _),"PC renewal credentials are excluded");
       var only = AchievementLabs.Core.MobileTransfer.Create("must-not-transfer","42","synthetic-event",null,true);
       using var onlyEnvelope=System.Text.Json.JsonDocument.Parse(only.Bytes);
       var onlyKey=only.PairingKey.Replace('-','+').Replace('_','/')+"=";
       using var onlyAes=new System.Security.Cryptography.AesGcm(Convert.FromBase64String(onlyKey),16);
       var onlyCipher=Convert.FromBase64String(onlyEnvelope.RootElement.GetProperty("ciphertext").GetString()!);var onlyPlain=new byte[onlyCipher.Length];
       onlyAes.Decrypt(Convert.FromBase64String(onlyEnvelope.RootElement.GetProperty("nonce").GetString()!),onlyCipher,Convert.FromBase64String(onlyEnvelope.RootElement.GetProperty("tag").GetString()!),onlyPlain,System.Text.Encoding.UTF8.GetBytes("AchievementLabs-PC-Transfer-v1"));
       using var onlyPayload=System.Text.Json.JsonDocument.Parse(onlyPlain);
       Assert(onlyPayload.RootElement.GetProperty("authorization").GetString()=="","Event-only transfer excludes account authorization");
       Assert(onlyPayload.RootElement.GetProperty("kind").GetString()=="eventToken","Event-only transfer cannot be mistaken for sign-in");
       System.Security.Cryptography.CryptographicOperations.ZeroMemory(onlyPlain);

       System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
   }
   Assert(Desc<ProgressBar>(window).Any(p=>p.Name=="LibraryRefreshProgressBar"),"Library refresh progress control present");
   Assert(Desc<TextBlock>(window).Any(p=>p.Name=="LibraryRefreshStatusText"),"Library refresh stage/stats present");
   Assert(Desc<ComboBox>(window).Any(c=>c.Name=="AchievementNameSort"),"Sort in real window");
   Assert(Desc<ComboBox>(window).Single(c=>c.Name=="AchievementNameSort").Parent is StackPanel { Name: "AchievementSortToolbar" },"Achievement sort sits in the top toolbar");
   Assert(!Desc<TabItem>(window).Any(t=>Equals(t.Header,"Steam")),"Steam queue is not a tab of the Xbox queue");
   Assert(!Desc<Button>(window).Any(b=>Equals(b.Content,"Legacy tools")),"Legacy tools navigation is removed");
   Assert(Desc<ComboBox>(window).Any(c=>c.Name=="AchievementPackFilter"),"Pack filter in real window");
   Assert(Desc<ListBox>(window).Any(l=>l.Name=="AchievementLabsAchievementList"),"Named real achievement list");
   Assert(Desc<Button>(window).Any(b=>(b.Content as string)=="Select multiple…"),"Batch button in real window");
      Assert(Desc<Button>(window).Count(b=>b.Name=="ExportAllAchievements")==1,"Single permanent Settings export button");
   Assert(Desc<Button>(window).Single(b=>b.Name=="ExportAllAchievements").Parent is StackPanel,"Export lives in Settings tile");
   Assert(Desc<Button>(window).Single(b=>b.Name=="RefreshDlcCatalogueNow").IsEnabled,"Public catalogue refresh available disconnected");
   Assert(Desc<Expander>(window).Any(e=>Equals(e.Header,"Debug")),"Debug section in Settings");
   Assert(Desc<Avalonia.Controls.Shapes.Path>(window).Any(c=>c.Name=="AchievementPageMythicIcon"),"Mythic icon in actual achievement page header");
   Assert(Desc<CheckBox>(window).Any(c=>c.Name=="MythicIconSetting"),"Mythic setting present");
   Assert(Desc<CheckBox>(window).Any(c=>c.Name=="CompletedDlcSetting"),"DLC colour setting present");
   Assert(Desc<StackPanel>(window).Count(b=>b.Name=="XboxRecordedPlaytime")==1,"Xbox-recorded playtime panel attached to spoofer");
   Assert(Desc<Button>(window).Single(b=>b.Name=="RefreshXboxPlaytime").IsEnabled==false,"Playtime disabled without connected account");
   Assert(Desc<Button>(window).Single(b=>b.Name=="CheckPresenceLogin").IsEnabled==false,"Read-only login check disabled disconnected");
   Assert(!Desc<Button>(window).Any(b=>b.Name is "HomeGrabEventToken" or "GrabEventTokenShortcut" or "GetEventTokenNow"),"Background token-capture controls remain removed");
   Assert(!Desc<Button>(window).Single(b=>b.Name=="AcquireConnectedAccountEventToken").IsEnabled,"Explicit matching-account token request disabled disconnected");
   Assert(Desc<CheckBox>(window).Single(b=>b.Name=="ShowEventToken").IsChecked==false,"Token hidden by default");
   Assert(Desc<Button>(window).Count(b=>b.Name=="OpenGameAutoSpoofer")==1,"Game queue shortcut attached");
   Assert(!Desc<Button>(window).Single(b=>b.Name=="OpenGameAutoSpoofer").IsVisible && Equals(Desc<Button>(window).Single(b=>b.Name=="OpenGameTitleSpoofer").Content,"Open in Game session"),"One visible game-session shortcut replaces the separate queue shortcut");
   Assert(Desc<Button>(window).Single(b=>b.Name=="OpenGameAutoSpoofer").Parent is Grid, "Game shortcut sits in filter toolbar");
   Assert(Desc<SelectableTextBlock>(window).Any(), "Game titles support text selection");
   Assert(!Desc<Button>(window).Any(b=>b.Name=="StartQueueSpoofing"), "Queue start spoofing control removed");
   Assert(Desc<CheckBox>(window).Any(b=>b.Name=="StopQueueOnSpoofFailure"), "Spoof failure stop option on queue page");
   Assert(Desc<CheckBox>(window).Any(b=>b.Name=="NotifyQueueCompletion"), "Queue completion notification option present");
   var queueOptions=Desc<WrapPanel>(window).Single(b=>b.Name=="QueueUnlockOptions");
   Assert(Desc<CheckBox>(queueOptions).Count()==4, "All four queue toggles share one wrapping row");
   Assert(Desc<CheckBox>(queueOptions).Any(b=>b.Name=="StopQueueOnSpoofFailure") && Desc<CheckBox>(queueOptions).Any(b=>b.Name=="NotifyQueueCompletion"), "Spoofer and completion toggles share the existing options row");
   Assert(Desc<Button>(window).Any(b=>Equals(b.Content,"Save queue delays")), "Manual delay save present");
   Assert(Desc<CheckBox>(window).Any(b=>Equals(b.Content,"Stop on unlock failure")), "Failure stop toggle present");
   Assert(Desc<Button>(window).Count(b=>b.Name=="RemoveQueueAchievements")==1,"Queue removal attached");
   Assert(Desc<Button>(window).Count(b=>b.Name=="RemoveCompletedQueueAchievements")==1,"Completed queue cleanup attached");
   Assert(Desc<Button>(window).Count(b=>b.Name=="OpenGameTitleSpoofer")==1,"Game spoofer shortcut present");
   var searchBox=Desc<TextBox>(window).First(b=>b.PlaceholderText=="Search achievements…");
   Assert(searchBox.InnerRightContent is Button,"Search clearing control attached");
   searchBox.Text="test search";Dispatcher.UIThread.RunJobs();
   Click((Button)searchBox.InnerRightContent!);Dispatcher.UIThread.RunJobs();
   Assert(searchBox.Text=="","Search clear immediately empties bound input");
   Assert(Desc<Button>(window).Any(b=>Equals(b.Content,"Load saved queue")),"Saved queue load button relabelled");
   {
    var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic;
    var model=window.GetType().GetField("model",flags)!.GetValue(window)!;
    var gameProperty=model.GetType().GetProperty("SelectedGame",flags)!;
    var syntheticGame=new AchievementLabs.Desktop.Game("572802557","Call of Duty: Ghosts","XboxOne",0,91,0);
    var gameId=gameProperty.PropertyType.GetProperty("TitleId")??gameProperty.PropertyType.GetProperty("Id");
    Assert(gameId!=null,"Actual game exposes title identifier");
    gameId!.SetValue(syntheticGame,"572802557");gameProperty.SetValue(model,syntheticGame);
    QueueTools.OpenForGame(model);
    var actualQueue=model.GetType().GetProperty("XboxQueue",flags)!.GetValue(model)!;
    Assert(Equals(actualQueue.GetType().GetProperty("TitleId")!.GetValue(actualQueue),"572802557"),"Actual game ID reaches automatic queue");
    Assert(Equals(model.GetType().GetProperty("IsQueues")!.GetValue(model),true),"Shortcut navigates to queue page");
    Assert(Equals(actualQueue.GetType().GetProperty("IsRunning")!.GetValue(actualQueue),false),"Shortcut does not start queue");
    var clientType=model.GetType().GetField("client",flags)!.FieldType;
    Assert(clientType.GetField("_xauth",flags)?.FieldType==typeof(string),"Existing client authentication integration");
    Assert(model.GetType().GetField("session",flags)!.FieldType.GetProperty("Xuid")!=null,"Existing session account integration");
    var input=Desc<TextBox>(window).Single(t=>t.PlaceholderText=="Paste event token");
    var tokenSession=new AchievementLabs.Core.ConnectedXboxSession("XBL3.0 x=synthetic;auth", "123", "");
    model.GetType().GetField("session",flags)!.SetValue(model,tokenSession);
    const string displayToken="x:XBL3.0 x=synthetic;display-token";
    ((AchievementLabs.Desktop.DesktopModel)model).EventTokenInput=displayToken;
    Dispatcher.UIThread.RunJobs();Assert(input.Text==displayToken,"Manual token reaches actual bound paste box");
    var reveal=Desc<CheckBox>(window).Single(t=>t.Name=="ShowEventToken");
    Assert(input.PasswordChar!='\0',"Actual token box masked initially");reveal.IsChecked=true;Assert(input.PasswordChar=='\0',"Reveal works");reveal.IsChecked=false;
    ((AchievementLabs.Desktop.DesktopModel)model).EventTokenInput="";Dispatcher.UIThread.RunJobs();Assert(input.Text=="","Clearing removes token from actual box");
    model.GetType().GetField("session",flags)!.SetValue(model,null);
    var typedModel=(AchievementLabs.Desktop.DesktopModel)model;
    typedModel.Navigate("SteamQueue"); Dispatcher.UIThread.RunJobs();
    Assert(!Desc<Button>(window).Single(b=>b.Name=="XboxAutoUnlockNav").Classes.Contains("active") && Desc<Button>(window).Single(b=>b.Name=="SteamAutoUnlockNav").Classes.Contains("active"),"Steam queue highlights only its Steam navigation child");
    typedModel.Navigate("Queues"); Dispatcher.UIThread.RunJobs();
    Assert(Desc<Button>(window).Single(b=>b.Name=="XboxAutoUnlockNav").Classes.Contains("active") && !Desc<Button>(window).Single(b=>b.Name=="SteamAutoUnlockNav").Classes.Contains("active"),"Xbox queue keeps its independent navigation");
    var libraryRow=Desc<ListBox>(window).Single(l=>l.Name=="XboxLibraryList").ItemTemplate!.Build(new AchievementLabs.Desktop.Game("1","Fixture","XboxOne",0,1,0))!;
    Assert(libraryRow.GetLogicalDescendants().OfType<Button>().Select(b=>b.Content?.ToString()).SequenceEqual(new[]{"Copy ID","Open achievements","Open stats","Open Game Session"}),"Library has four actions including the unified game session");
    var spooferPlaytime=Desc<TextBlock>(window).Single(t=>t.Name=="QueueRecordedPlaytime");
    Assert(Desc<TextBlock>(window).Any(t=>t.Text=="TITLE SPOOFER"), "Separate spoofer status tile present in Auto Unlock");
    typedModel.GetType().GetProperty("ActiveSpoofTitle")!.SetValue(typedModel,"Synthetic title · 42");
    typedModel.GetType().GetProperty("PresenceElapsed")!.SetValue(typedModel,"Session: 26.50 hours");
    Dispatcher.UIThread.RunJobs();
    Assert(Desc<SelectableTextBlock>(window).Any(t=>t.Text=="Synthetic title · 42") && Desc<TextBlock>(window).Any(t=>t.Text=="Session: 26.50 hours"), "Active title and total hours propagate to queue tile");
    var updateElapsed=typedModel.GetType().GetMethod("UpdatePresenceElapsed",flags)!;
    updateElapsed.Invoke(typedModel,new object[] { TimeSpan.FromHours(26)+TimeSpan.FromMinutes(30)+TimeSpan.FromSeconds(3) });
    Dispatcher.UIThread.RunJobs();
    Assert(Desc<TextBlock>(window).Single(t=>t.Name=="SpooferSessionTimer").Text=="Elapsed: 26:30:03", "Spoofer page has elapsed timer with total hours beyond 24");
    Assert(Desc<TextBlock>(window).Single(t=>t.Name=="QueueSpooferSessionTimer").Text=="Elapsed: 26:30:03", "Auto Unlock shows the same elapsed timer");
    updateElapsed.Invoke(typedModel,new object[] { TimeSpan.FromSeconds(1) });Dispatcher.UIThread.RunJobs();
    Assert(Desc<TextBlock>(window).Single(t=>t.Name=="SpooferSessionTimer").Text=="Elapsed: 00:00:01" && Desc<TextBlock>(window).Single(t=>t.Name=="QueueSpooferSessionTimer").Text=="Elapsed: 00:00:01", "Both timers update to the next elapsed value");
    updateElapsed.Invoke(typedModel,new object[] { TimeSpan.Zero });Dispatcher.UIThread.RunJobs();
    Assert(Desc<TextBlock>(window).Single(t=>t.Name=="SpooferSessionTimer").Text=="Elapsed: 00:00:00", "Timer resets for a new session");
    var playtimeValue=Desc<StackPanel>(window).Single(t=>t.Name=="XboxRecordedPlaytime").Children.OfType<TextBlock>().ElementAt(1);
    playtimeValue.Text="123.45 hours";Dispatcher.UIThread.RunJobs();
    Assert(spooferPlaytime.Text=="Xbox-recorded playtime: 123.45 hours", "Queue mirrors existing playtime reader");
    Assert(Desc<Button>(window).Any(b=>Equals(b.Content,"Test Windows notification")), "Windows notification test control present");
    Assert(!Desc<ListBox>(window).Single(l=>l.Name=="XboxLibraryList").AutoScrollToSelectedItem,"Library selection does not force scrolling after sort or refresh");
    var queueList=Desc<ListBox>(window).Single(l=>l.Name=="XboxAutoUnlockList");
    typedModel.XboxQueue.QueueItems.Add(new AchievementLabs.Desktop.Workflows.AutoUnlockerViewModel.AutoUnlockQueueDisplay { Status="Unlocked", CanEditDelay=true });
    typedModel.XboxQueue.IsRunning=true;typedModel.XboxQueue.IsConfigEnabled=false;Dispatcher.UIThread.RunJobs();
    Assert(queueList.IsEnabled && !typedModel.XboxQueue.QueueItems[0].CanEditDelay,"Running queue list remains enabled while delay controls are locked");
    typedModel.XboxQueue.IsRunning=false;typedModel.XboxQueue.IsConfigEnabled=true;
    window.Show();var export=ExportAllView.Show(window,model);Dispatcher.UIThread.RunJobs();
    var dialog=window.OwnedWindows.Single();
    Assert(Desc<ProgressBar>(dialog).Any(b=>b.Name=="AllAchievementsProgress"),"Export progress bar present");
    Assert(Desc<CheckBox>(dialog).Single().IsChecked==false,"Skip completed titles by default");
    Assert(Desc<Button>(dialog).Single(b=>Equals(b.Content,"Pause")).IsEnabled==false,"Pause idle state");
    dialog.Close();Dispatcher.UIThread.RunJobs();Assert(export.IsCompleted,"Export dialog closes without scan");
   }
   Console.WriteLine("PASS: actual patched application initializes headlessly; main sorting, pack filter, named list and multi-select button attached.");
   if(args.Contains("--scroll"))
   {
    var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic;
    object realModel=window.GetType().GetField("model",flags)!.GetValue(window)!;
    var gameType=assembly.GetTypes().Single(t=>t.Name=="Game");
    object game=System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(gameType);
    foreach(var property in gameType.GetProperties().Where(p=>p.CanWrite && p.PropertyType==typeof(string)))property.SetValue(game,"");
    gameType.GetProperty("Name")!.SetValue(game,"Call of Duty: Ghosts");
    gameType.GetProperty("Id")!.SetValue(game,"572802557");
    realModel.GetType().GetField("selectedGame",flags)!.SetValue(realModel,game);
    var rowType=assembly.GetTypes().Single(t=>t.Name=="Achievement");
    var rowCtor=rowType.GetConstructors().Single(c=>c.GetParameters().Length==8);
    Array data=Array.CreateInstance(rowType,91);var names=AchievementView.Ghosts.Keys.ToArray();
    for(int i=0;i<91;i++)data.SetValue(rowCtor.Invoke(new object[]{(i+1).ToString(),names[i],"Scrolling regression test",20,false,true,true,""}),i);
    var actualList=Desc<ListBox>(window).Single(l=>l.Name=="AchievementLabsAchievementList");
    ((Panel)actualList.Parent!).Children.Remove(actualList);
    var host=new Window{Width=800,Height=360,Content=actualList};actualList.ItemsSource=data;host.Show();host.UpdateLayout();
    int headingCount=0;
    foreach(object row in data)
    {
      var built=actualList.ItemTemplate!.Build(row)!;
      headingCount+=built.GetLogicalDescendants().OfType<Border>().Count(b=>b.Name=="AchievementPackDivider" && b.IsVisible);
    }
    Assert(headingCount==5,"Actual row template emits exactly five visible pack headings");
    for(int round=0;round<3;round++)
    {
      for(int i=0;i<91;i++){actualList.ScrollIntoView(i);host.UpdateLayout();Dispatcher.UIThread.RunJobs();}
      for(int i=90;i>=0;i--){actualList.ScrollIntoView(i);host.UpdateLayout();Dispatcher.UIThread.RunJobs();}
      actualList.ItemsSource=null;host.UpdateLayout();actualList.ItemsSource=data;host.UpdateLayout();
    }
    host.Close();Console.WriteLine("PASS: actual achievement template, 546 scroll targets plus clearing and repopulating the virtualized list.");
   }
   window.Close();return;
  }

  AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
  Application.Current!.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
  var m=new Model();int n=0;
  foreach(bool events in new[]{false,true})foreach(bool unlocked in new[]{false,true})foreach(bool mapping in new[]{false,true})foreach(bool supported in new[]{false,true})foreach(bool definition in new[]{false,true})
  {
   m.eventBased=events;m.eventTitleSupported=supported;m.mappedIds=mapping?new(){"1"}:new();m.definitions=definition?new(){{"1",new object()}}:new();
   bool expected=definition && (events ? mapping && supported : !unlocked);
   Assert((BatchPicker.IneligibleReason(m,new Row("1","Row",unlocked))==null)==expected,"Eligibility matrix");n++;
   m.SelectedAchievement=new Row("1","Row",unlocked);
   Assert(BatchPicker.CanUnlockSelected(m)==expected,"Single retry eligibility matrix");
  }
  m=new Model();m.VisibleAchievements=new[]{new Row("1","Locked",false),new Row("2","Retry",true),new Row("3","Unmapped",false)};
  Assert(BatchPicker.EligibleIds(m,new[]{m.VisibleAchievements[0],m.VisibleAchievements[0],m.VisibleAchievements[1],m.VisibleAchievements[2]}).SequenceEqual(new[]{"1","2"}),"Filtering and duplicate removal");
  var w=new FakeWindow(m);w.Show();BatchPicker.Attach(w);
  Window d=Open(w);var checks=Desc<CheckBox>(d).ToArray();Assert(checks.Length==3 && !checks[2].IsEnabled,"Disabled mapping checkbox");
  Assert(!Desc<Button>(d).Single(b=>(b.Content as string)=="Unlock / retry selected").IsEnabled,"Empty selection disabled");
  SelectAll(d);Assert(checks.Count(c=>c.IsChecked==true)==2,"Select all eligible");
  Click(Desc<Button>(d).Single(b=>(b.Content as string)=="Clear"));Assert(checks.All(c=>c.IsChecked!=true),"Clear all");
  checks[1].IsChecked=true;Submit(d);Dispatcher.UIThread.RunJobs();Assert(m.Sent.SequenceEqual(new[]{"2"}),"Only chosen retry submitted");
  d=Open(w);SelectAll(d);Submit(d);Dispatcher.UIThread.RunJobs();Assert(m.Sent.SequenceEqual(new[]{"1","2"}) && m.Calls==2,"One batch call for multiple checked achievements");
  d=Open(w);SelectAll(d);Click(Desc<Button>(d).Single(b=>(b.Content as string)=="Cancel"));Assert(m.Calls==2,"Cancel sends nothing");
  d=Open(w);SelectAll(d);m.SelectedGame=new Game();Submit(d);Assert(m.Calls==2 && d.IsVisible,"Stale game rejected");d.Close();Dispatcher.UIThread.RunJobs();
  m.CanQuery=false;m.Notify();Assert(!Desc<Button>(w).Single(b=>(b.Content as string)=="Select multiple…").IsEnabled,"Busy disables batch button");w.Close();
  Assert(AchievementView.Ghosts.Count==91,"All 91 mapped");
  Assert(AchievementView.Packs.Skip(1).Take(5).Select(p=>AchievementView.Ghosts.Values.Count(v=>v==p)).SequenceEqual(new[]{50,11,10,10,10}),"Pack counts 50/11/10/10/10");
  Assert(!AchievementView.Supports(new Game {Id="wrong",Name="Call of Duty: Ghosts"}),"Wrong version never matched by name");
  Assert(AchievementView.Supports(new Game {Id="572802557",Name="Localized title"}),"Exact Title ID matches independently of name");
  object[] boundaries={new Row("1","Ghost Stories",false),new Row("2","Spatial Awareness",false),new Row("52","Undiscovered Truths",false),new Row("53","Pushing Ahead",false),new Row("92","Hat Trick",false)};
  Assert(boundaries.Count(r=>AchievementView.StartsGroup(boundaries,r))==3,"One divider per visible pack");
  var filtered=boundaries.Skip(3).ToArray();Assert(AchievementView.StartsGroup(filtered,filtered[0]),"Filtered pack starts with a divider");
  Assert(!AchievementView.StartsGroup(null,boundaries[0]),"Cleared virtual list safe");
  Assert(AchievementView.Group(new Row("92","Hat Trick",true))=="Nemesis","Hat Trick is Nemesis");
  Assert(AchievementView.Group(new Row("x","UNKNOWN",false))=="Unclassified","Unknown not guessed as base");
  Assert(AchievementView.Group(new Row("x","YOU’VE EARNED IT",false))=="Base game","Punctuation and case matching");
  var sortRows=new[]{new Row("z","zebra",false),new Row("a","Apple",false),new Row("b","banana",false)};
  Assert(AchievementView.Arrange(sortRows,false,1,"All packs").Cast<Row>().Select(r=>r.Id).SequenceEqual(new[]{"a","b","z"}),"A-Z sorting");
  Assert(AchievementView.Arrange(sortRows,false,2,"All packs").Cast<Row>().Select(r=>r.Id).SequenceEqual(new[]{"z","b","a"}),"Z-A sorting");
  m=new Model {SelectedGame=new Game {Id="572802557",Name="Call of Duty®: Ghosts"},VisibleAchievements=new[]{new Row("1","Hat Trick",true),new Row("2","Always Hard",false),new Row("3","Ghost Stories",false)}};
  m.mappedIds.Add("3");
  var state=AchievementView.For(m);state.Sort=1;state.Pack="Nemesis";
  var transformed=AchievementView.Transform(m.VisibleAchievements,m);
  Assert(transformed is Row[] && transformed.Cast<Row>().Select(r=>r.Id).SequenceEqual(new[]{"2","1"}),"Typed pack filter and alphabetical sort");
  state.Pack="All packs";
  w=new FakeWindow(m);w.Show();BatchPicker.Attach(w);
  var mainSort=Desc<ComboBox>(w).Single(c=>c.Name=="AchievementNameSort");mainSort.SelectedIndex=2;
  Assert(AchievementView.For(m).Sort==2,"Main sort control updates state");
  var mainPack=Desc<ComboBox>(w).Single(c=>c.Name=="AchievementPackFilter");mainPack.SelectedItem="Nemesis";
  Assert(AchievementView.For(m).Pack=="Nemesis","Main pack control updates state");
  mainPack.SelectedItem="All packs";
  d=Open(w);
  var pickerPack=Desc<ComboBox>(d).Single(c=>c.Name=="PickerPackFilter");pickerPack.SelectedItem="Nemesis";
  SelectAll(d);
  Assert(Desc<CheckBox>(d).Count()==2 && Desc<CheckBox>(d).All(c=>c.IsChecked==true),"Pick only displayed DLC");
  var pickerSort=Desc<ComboBox>(d).Single(c=>c.Name=="PickerNameSort");pickerSort.SelectedIndex=1;
  Assert(Desc<CheckBox>(d).All(c=>c.IsChecked==true),"Selections retained during sorting");
  pickerPack.SelectedItem="Base game";
  Assert(Desc<CheckBox>(d).Count()==1 && Desc<CheckBox>(d).Single().IsChecked!=true,"Other pack starts unchecked");
  Assert(Desc<TextBlock>(d).Any(t=>t.Text?.StartsWith("2 selected")==true),"Hidden selections counted");
  Click(Desc<Button>(d).Single(b=>(b.Content as string)=="Clear"));
  Assert(Desc<TextBlock>(d).Any(t=>t.Text?.StartsWith("0 selected")==true),"Clear includes hidden selections");
  SelectAll(d);Submit(d);Dispatcher.UIThread.RunJobs();Assert(m.Sent.SequenceEqual(new[]{"3"}),"Submit chosen pack only");
  m.SelectedGame=new Game();Assert(AchievementView.For(m).Pack=="All packs","Pack reset when switching game");w.Close();
  Console.WriteLine("PASS: 91-name mapping, pack counts, unknown handling, normalization, sorting, typed arrays, both sets of controls, selection preservation and pack-only submission.");
  Console.WriteLine($"PASS: {n} eligibility combinations, duplicates, checkbox selection, select all, clear, cancel, single batch submission, stale-game guard and busy state.");
 }
}
public record Row(string Id,string Name,bool Unlocked){ public int Score=>20; }
public class Game {public string Id{get;set;}="";public string Name{get;set;}="Test game";}
public class Model:INotifyPropertyChanged
{
 public Row? SelectedAchievement{get;set;}
 public bool CanQuery{get;set;}=true;public bool liveAchievements=true,eventBased=true,eventTitleSupported=true;
 public Dictionary<string,object> definitions=new(){{"1",new()},{"2",new()},{"3",new()}};
 public HashSet<string> mappedIds=new(){"1","2"};public Game SelectedGame{get;set;}=new();public Row[] VisibleAchievements{get;set;}=Array.Empty<Row>();
 public string ActionSummary{get;set;}="";public string[] Sent=Array.Empty<string>();public int Calls;
 public static bool UsesLegacyEndpoint(Game game)=>false;
 public Task SubmitAchievementsAsync(string[] ids){Sent=ids;Calls++;return Task.CompletedTask;}
 public event PropertyChangedEventHandler? PropertyChanged;
 protected void Changed(string name)=>PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(name));
 public void Notify()=>PropertyChanged?.Invoke(this,new PropertyChangedEventArgs("CanQuery"));
}
public class FakeWindow:Window
{
 public object model;
 public FakeWindow(Model m){model=m;Content=new StackPanel {Children={new Button{Content="Unlock / retry "}}};}
}


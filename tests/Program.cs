using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Reflection;
using System.ClientModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Microsoft.Extensions.AI;
using OpenAI;
using silaa.Core;
using silaa.Services;
using ChatMessage = silaa.Core.ChatMessage;

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void AssertThrows<TException>(Action action, string message) where TException : Exception
{
    try { action(); }
    catch (TException) { return; }
    throw new Exception(message);
}

static async Task AssertThrowsAsync<TException>(Func<Task> action, string message) where TException : Exception
{
    try { await action(); }
    catch (TException) { return; }
    throw new Exception(message);
}

if (args.Contains("--probe-current-llm-get", StringComparer.OrdinalIgnoreCase))
{
    var probeStore = new LocalStore();
    var runtime = probeStore.GetActiveRuntimeSettings();
    using var probeHandler = new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(10)
    };
    using var probeClient = new HttpClient(probeHandler)
    {
        Timeout = TimeSpan.FromSeconds(20),
        DefaultRequestVersion = HttpVersion.Version11,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
    };
    using var request = new HttpRequestMessage(
        HttpMethod.Get,
        new Uri(EndpointGuard.Parse(runtime.Settings.LlmEndpoint), "models"));
    var apiKey = runtime.ApiKey;
    if (!string.IsNullOrWhiteSpace(apiKey))
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    var started = Stopwatch.StartNew();
    using var response = await probeClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    Console.WriteLine($"PROBE_GET_STATUS={(int)response.StatusCode} {response.StatusCode}");
    Console.WriteLine($"PROBE_GET_HEADERS_MS={started.ElapsedMilliseconds}");
    return;
}

if (args.Contains("--probe-current-llm-python", StringComparer.OrdinalIgnoreCase))
{
    var probeStore = new LocalStore();
    var runtime = probeStore.GetActiveRuntimeSettings();
    if (string.IsNullOrWhiteSpace(runtime.ApiKey))
    {
        Console.WriteLine("PROBE_ERROR=active connection has no stored API key");
        return;
    }
    const string code = """
import json, os, requests, time
url = os.environ["SILAA_PROBE_ENDPOINT"].rstrip("/") + "/chat/completions"
payload = {"model": os.environ["SILAA_PROBE_MODEL"], "messages": [{"role": "user", "content": "Reply with OK only."}], "max_tokens": 32, "stream": False}
started = time.perf_counter()
r = requests.post(url, headers={"Authorization": "Bearer " + os.environ["SILAA_PROBE_KEY"], "Accept": "application/json"}, json=payload, timeout=(10, 60))
print("PROBE_PYTHON_STATUS=" + str(r.status_code))
print("PROBE_PYTHON_MS=" + str(round((time.perf_counter() - started) * 1000)))
print("PROBE_PYTHON_BODY=" + r.text[:4000])
""";
    var startInfo = new ProcessStartInfo("python")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };
    startInfo.ArgumentList.Add("-c");
    startInfo.ArgumentList.Add(code);
    startInfo.Environment["SILAA_PROBE_ENDPOINT"] = runtime.Settings.LlmEndpoint;
    startInfo.Environment["SILAA_PROBE_MODEL"] = runtime.Settings.Model;
    startInfo.Environment["SILAA_PROBE_KEY"] = runtime.ApiKey;
    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Python probe could not start.");
    var stdoutTask = process.StandardOutput.ReadToEndAsync();
    var stderrTask = process.StandardError.ReadToEndAsync();
    using var probeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
    try
    {
        await process.WaitForExitAsync(probeTimeout.Token);
    }
    catch (OperationCanceledException)
    {
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        Console.WriteLine("PROBE_PYTHON_ERROR=process exceeded 75 seconds");
    }
    var stdout = await stdoutTask;
    var stderr = await stderrTask;
    Console.Write(stdout);
    if (!string.IsNullOrWhiteSpace(stderr)) Console.WriteLine("PROBE_PYTHON_ERROR=" + stderr.Trim());
    return;
}

if (args.Contains("--probe-current-llm", StringComparer.OrdinalIgnoreCase))
{
    var probeStore = new LocalStore();
    var runtime = probeStore.GetActiveRuntimeSettings();
    var apiKey = runtime.ApiKey;
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        Console.WriteLine("PROBE_ERROR=active connection has no stored API key");
        return;
    }
    using var probeHandler = new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(15)
    };
    using var probeClient = new HttpClient(probeHandler)
    {
        Timeout = TimeSpan.FromSeconds(60),
        DefaultRequestVersion = HttpVersion.Version11,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
    };
    using var request = new HttpRequestMessage(
        HttpMethod.Post,
        new Uri(EndpointGuard.Parse(runtime.Settings.LlmEndpoint), "chat/completions"));
    request.Version = HttpVersion.Version11;
    request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    request.Headers.Accept.ParseAdd("application/json");
    request.Content = new StringContent(JsonSerializer.Serialize(new
    {
        model = runtime.Settings.Model,
        messages = new[] { new { role = "user", content = "Reply with OK only." } },
        max_tokens = 32,
        stream = false
    }), Encoding.UTF8, "application/json");
    using var response = await probeClient.SendAsync(request);
    var body = await response.Content.ReadAsStringAsync();
    Console.WriteLine($"PROBE_STATUS={(int)response.StatusCode} {response.StatusCode}");
    Console.WriteLine("PROBE_BODY=" + (body.Length <= 4000 ? body : body[..4000] + "…"));
    return;
}

if (args.Contains("--probe-current-ppt", StringComparer.OrdinalIgnoreCase))
{
    var probeStore = new LocalStore();
    await using var probeHarness = new LlmHarness(probeStore);
    string? createdPath = null;
    try
    {
        createdPath = await probeHarness.CreatePresentationFromSurveyAsync(
            new silaa.ViewModels.PresentationSurvey(
                "silaa-mono", "연결 호환성 확인", "내부 검증", "3",
                "간결하고 전문적", "요약, 다음 단계", $"silaa-ppt-probe-{Guid.NewGuid():N}"));
        var extracted = DocumentArtifactService.ReadText(createdPath);
        Console.WriteLine($"PPT_PROBE_STATUS=OK");
        Console.WriteLine($"PPT_PROBE_READBACK={(extracted.Length > 0 ? "OK" : "EMPTY")}");
    }
    finally
    {
        if (createdPath is not null && File.Exists(createdPath)) File.Delete(createdPath);
    }
    return;
}

if (args.Contains("--probe-current-template", StringComparer.OrdinalIgnoreCase))
{
    var probeStore = new LocalStore();
    await using var probeHarness = new LlmHarness(probeStore);
    try
    {
        var design = await probeHarness.GenerateDocumentTemplateAsync(
            "PPT 임원 보고용. 흑백 기반의 엄중하고 고급스러운 분위기, 넓은 여백과 명확한 숫자 강조.");
        Assert(design.Format is "docx" or "pptx" or "html", "AI template format should be supported");
        Assert(design.Background.Length == 6 && design.AccentColor.Length == 6,
            "AI template colors should be normalized RGB hex values");
        Console.WriteLine($"TEMPLATE_PROBE_STATUS=OK format={design.Format} name={design.Name} layout={design.Layout}");
    }
    catch (Exception exception)
    {
        Console.WriteLine($"TEMPLATE_PROBE_STATUS=FAILED {exception.Message}");
        Environment.ExitCode = 1;
    }
    return;
}

static void SaveLayoutSnapshot(Control control, string outputPath, double width = 1240, double height = 800)
{
    var ownsHost = control is not Window;
    var host = control as Window ?? new Window
    {
        Content = control,
        CanResize = false
    };
    host.Width = width;
    host.Height = height;
    host.WindowState = WindowState.Maximized;
    host.Show();
    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    host.Measure(new Size(width, height));
    host.Arrange(new Rect(0, 0, width, height));
    host.UpdateLayout();
    var scaling = host.RenderScaling;
    var pixelSize = new PixelSize(
        Math.Max(1, (int)Math.Ceiling(host.ClientSize.Width * scaling)),
        Math.Max(1, (int)Math.Ceiling(host.ClientSize.Height * scaling)));
    using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96 * scaling, 96 * scaling));
    bitmap.Render(host);
    bitmap.Save(outputPath, PngBitmapEncoderOptions.Default);
    if (ownsHost)
    {
        host.Content = null;
        host.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }
}

var recoverableWebViewAbort = new System.Runtime.InteropServices.COMException(
    "WebView initialization was aborted", unchecked((int)0x80004004))
{
    Source = "Avalonia.Controls.WebView2"
};
Assert(silaa.CrashLog.IsRecoverableUiException(recoverableWebViewAbort),
    "WebView2 E_ABORT during window teardown is treated as recoverable");
Assert(!silaa.CrashLog.IsRecoverableUiException(new System.Runtime.InteropServices.COMException(
        "Unexpected COM failure", unchecked((int)0x80004005))),
    "unrelated COM failures remain fatal and visible");

if (args.Contains("--xaml-smoke", StringComparer.OrdinalIgnoreCase))
{
    AppBuilder.Configure<silaa.SilaaApplication>().UsePlatformDetect().SetupWithoutStarting();
    var xamlRoot = Path.Combine(Path.GetTempPath(), "silaa-xaml-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(xamlRoot);
    try
    {
        var store = new LocalStore(xamlRoot);
        var harness = new LlmHarness(store);
        var settings = new silaa.UI.SettingsPage(store, harness);
        var settingsViewModel = (silaa.ViewModels.SettingsViewModel)settings.DataContext!;
        Assert(settingsViewModel.IsConnectionList && !settingsViewModel.IsEditorOpen,
            "LLM settings opens on the connection list instead of the detail form");
        settingsViewModel.NewConnectionCommand.Execute(null);
        Assert(settingsViewModel.IsEditorOpen && !settingsViewModel.IsConnectionList,
            "new LLM connection opens the detail editor on demand");
        settingsViewModel.BackCommand.Execute(null);
        Assert(settingsViewModel.IsConnectionList && !settingsViewModel.IsEditorOpen,
            "LLM detail back action returns to the connection list");
        var workspaceSettings = new silaa.UI.WorkspaceSettingsPage(store);
        var home = new silaa.UI.HomePage();
        var window = new silaa.UI.MainWindow(store, harness);
        var navigation = new silaa.ViewModels.NavigationViewModel(
            () => Task.CompletedTask, () => Task.CompletedTask, () => { }, () => { }, () => { }, () => { }, () => { });
        var menu = new silaa.UI.MainMenuView { DataContext = navigation };
        menu.Measure(new Size(232, 800));
        menu.Arrange(new Rect(0, 0, 232, 800));
        var menuButtons = menu.GetLogicalDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("nav-item") || button.Classes.Contains("new-task"))
            .ToList();
        var menuWidths = menuButtons.Select(button => Math.Round(button.Bounds.Width, 1)).ToList();
        Assert(menuButtons.Count == 10 && menuButtons.All(button => button.HorizontalAlignment == Avalonia.Layout.HorizontalAlignment.Stretch) &&
               menuWidths.Distinct().Count() == 1,
            $"main sidebar actions share one stretched width: {string.Join(',', menuWidths)}");
        Assert(menu.GetLogicalDescendants().OfType<Material.Icons.Avalonia.MaterialIcon>().Count() >= 7,
            "main sidebar uses the shared Material icon set instead of hand-authored glyph paths");
        Assert(menu.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "홈") &&
               menu.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "앱") &&
                menu.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "예약 작업") &&
                menu.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "내 일정") &&
               menu.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "기억") &&
               menu.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "문서 템플릿") &&
               menu.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "사용법 물어보기") &&
               !menu.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "Workspace"),
            "workspace sidebar uses the requested Home, Conversation, Projects, Automation, Apps labels");
        var settingsMenu = new silaa.UI.SettingsMenuView { DataContext = navigation };
        settingsMenu.Measure(new Size(232, 800));
        settingsMenu.Arrange(new Rect(0, 0, 232, 800));
        Assert(settingsMenu.GetLogicalDescendants().OfType<Button>()
                   .Count(button => button.Classes.Contains("settings-nav")) == 5 &&
               !settingsMenu.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "기억"),
            "settings sidebar owns LLM, folder, security, Vault, and main-menu navigation without Memory");
        Assert(home.FindControl<TextBox>("QuickPromptBox") is not null &&
               home.GetLogicalDescendants().OfType<Button>().Count(button => button.Classes.Contains("launch-card")) == 3,
            "home exposes Enter-ready input and exactly three primary start paths");
        Assert(home.FindControl<StackPanel>("MyAppsSection") is not null,
            "home always exposes the My Apps section even before an app project exists");
        var documentTemplateStore = new DocumentTemplateStore(xamlRoot);
        var generatedTemplateBriefs = new List<string>();
        Task<DocumentDesignTemplate> GenerateTemplateForTest(string brief)
        {
            generatedTemplateBriefs.Add(brief);
            var format = brief.Contains("html", StringComparison.OrdinalIgnoreCase) ? "html"
                : brief.Contains("ppt", StringComparison.OrdinalIgnoreCase) ? "pptx" : "docx";
            var name = brief.Contains("brief.html", StringComparison.OrdinalIgnoreCase) ? "brief" : "AI Mono Report";
            return Task.FromResult(new DocumentDesignTemplate(
                name, name, "AI가 요청을 해석해 만든 테스트 디자인", "0C0C0C", "FFFFFF", "D4D4D4",
                "6EA8FE", "858585", "Malgun Gothic", "spacious", false, format));
        }
        var presentationViewModel = new silaa.ViewModels.PresentationTemplatesViewModel(
            navigation, documentTemplateStore, GenerateTemplateForTest);
        var presentationPage = new silaa.UI.PresentationTemplatesPage { DataContext = presentationViewModel };
        Assert(presentationPage.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "문서 템플릿"),
            "workspace document template catalog loads");
        Assert(presentationViewModel.IsCatalog && presentationViewModel.Templates.Count == 6,
            "DOCS, PPT, and HTML templates open as visual preview cards");
        presentationViewModel.Templates.First(card => card.Id == "executive-navy").SelectCommand.Execute(null);
        Assert(presentationViewModel.IsEditor && documentTemplateStore.DefaultId("pptx") == "silaa-mono" &&
               presentationViewModel.EditorMessages.Count == 1,
            "selecting a design card opens the split AI editor without changing the default yet");
        presentationViewModel.EditorInputText = "강조색을 선명한 파랑으로 바꾸고 제목을 더 크게 보여줘";
        await presentationViewModel.SendEditorCommand.ExecuteAsync(null);
        Assert(presentationViewModel.IsEditor && presentationViewModel.EditingTitle == "AI Mono Report" &&
               generatedTemplateBriefs.Count == 1,
            "template editor sends the current design and chat request to AI and refreshes the live preview");
        presentationViewModel.ApplyEditorCommand.Execute(null);
        Assert(presentationViewModel.IsCatalog && documentTemplateStore.Resolve("pptx", "default").Name == "AI Mono Report",
            "applying an edited preview saves a custom copy as the format default");
        presentationViewModel.AddTemplateCommand.Execute(null);
        Assert(presentationViewModel.IsConversation && presentationViewModel.Messages.Count == 1 &&
               presentationViewModel.Messages.All(message => message.IsAssistant),
            "new design asks one natural-language AI design question");
        await presentationViewModel.GenerateAndSaveAsync("Docs 임원 보고서, 엄중한 흑백과 넓은 여백");
        Assert(presentationViewModel.IsCatalog && presentationViewModel.Templates.Count == 8 &&
               documentTemplateStore.Resolve("docx", "default").Name == "AI Mono Report" &&
                generatedTemplateBriefs.Count == 2,
            "one brief is delegated to AI and the generated reusable template is selected");
        var presentationRoot = (Grid)presentationPage.Content!;
        Assert(presentationRoot.ColumnDefinitions[0].Width.IsAuto,
            "presentation page sidebar uses an Auto GridLength instead of binding a Double resource to GridLength");
        var openPresentationTemplates = typeof(silaa.UI.MainWindow).GetMethod(
            "OpenPresentationTemplates", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("OpenPresentationTemplates method not found");
        openPresentationTemplates.Invoke(window, null);
        var navigationPageHost = window.FindControl<Grid>("PageHost") ?? throw new Exception("PageHost not found");
        Assert(navigationPageHost.Children.SingleOrDefault() is silaa.UI.PresentationTemplatesPage,
            "the exact MainWindow PPT navigation path attaches the survey page without a GridLength crash");

        var documentRoot = Path.Combine(xamlRoot, "document-tests");
        Directory.CreateDirectory(documentRoot);
        var customDocsDesign = documentTemplateStore.Resolve("docx", "default");
        var docx = DocumentArtifactService.Create(
            Path.Combine(documentRoot, "report.docx"), "docx", "보고서", "# 개요\n문서 본문",
            customDocsDesign.Id, customDocsDesign);
        var xlsx = DocumentArtifactService.Create(Path.Combine(documentRoot, "table.xlsx"), "xlsx", "표", "이름,값\nA,10");
        var customPresentationDesign = documentTemplateStore.Resolve("pptx", "default");
        var pptx = DocumentArtifactService.Create(
            Path.Combine(documentRoot, "deck.pptx"), "pptx", "분기 보고",
            "현황\n- 매출 증가\n---\n다음 계획\n- 출시", customPresentationDesign.Id, customPresentationDesign);
        var htmlDesign = documentTemplateStore.Resolve("html", "default");
        var html = DocumentArtifactService.Create(
            Path.Combine(documentRoot, "brief.html"), "html", "웹 보고서", "# 핵심\n- 첫 번째 항목",
            htmlDesign.Id, htmlDesign);
        var pdf = DocumentArtifactService.Create(Path.Combine(documentRoot, "brief.pdf"), "pdf", "간단 보고", "핵심 내용입니다.");
        Assert(DocumentArtifactService.ReadText(docx).Contains("문서 본문") &&
               DocumentArtifactService.ReadText(xlsx).Contains("A\t10") &&
               DocumentArtifactService.ReadText(pptx).Contains("다음 계획") &&
               DocumentArtifactService.ReadText(html).Contains("웹 보고서") &&
               DocumentArtifactService.ReadText(pdf).Contains("핵심 내용"),
            "DOCX, XLSX, PPTX, HTML, and PDF can be created and read back");
        Assert(File.ReadAllText(html).Contains("--accent:#" + htmlDesign.AccentColor, StringComparison.Ordinal),
            "HTML documents embed the selected visual template palette");
        var importedDocsTemplate = DocumentTemplateImporter.Import(docx);
        Assert(importedDocsTemplate.Format == "docx" && importedDocsTemplate.Name == "report",
            "an existing DOCX can be analyzed into a reusable design template");
        var importedPptTemplate = DocumentTemplateImporter.Import(pptx);
        Assert(importedPptTemplate.Format == "pptx" && importedPptTemplate.Background == customPresentationDesign.Background,
            "an existing PPTX can be analyzed into a reusable design template");
        await presentationViewModel.ImportFileAsync(html);
        Assert(presentationViewModel.Templates.Count == 9 &&
               documentTemplateStore.Resolve("html", "default").Name == "brief" &&
                generatedTemplateBriefs.Count == 3,
            "uploading HTML sends extracted design evidence to AI and selects its generated card");
        using (var presentation = PresentationDocument.Open(pptx, false))
        {
            var validationErrors = new OpenXmlValidator().Validate(presentation).ToList();
            Assert(validationErrors.Count == 0,
                "generated PPTX passes Open XML validation: " + string.Join(" | ", validationErrors.Select(error => error.Description)));
            Assert(presentation.PresentationPart!.SlideParts
                    .SelectMany(slide => slide.Slide?.Descendants<DocumentFormat.OpenXml.Drawing.RgbColorModelHex>() ?? [])
                    .Any(color => color.Val?.Value == customPresentationDesign.Background),
                "the reusable custom design palette is embedded into generated PPT slides");
        }
        var sourceAttachment = Path.Combine(documentRoot, "drag.txt");
        File.WriteAllText(sourceAttachment, "attachment");
        var importedAttachment = AttachmentService.Import(sourceAttachment, xamlRoot);
        Assert(File.Exists(importedAttachment) && importedAttachment.Contains(Path.Combine(".silaa", "attachments")),
            "dragged files are copied into the owned attachment area");
        var oneDrive = store.AppConnections.SaveOneDrive("업무 OneDrive", documentRoot);
        var gitHub = store.AppConnections.SaveGitHub("개인 GitHub", "tester", "test-token");
        Assert(store.AppConnections.List().Count == 2 && store.AppConnections.GetToken(gitHub.Id) == "test-token" &&
               store.AppConnections.Get(oneDrive.Id)?.RootPath == Path.GetFullPath(documentRoot),
            "GitHub credentials stay in Vault while OneDrive keeps only its approved sync root");
        store.AppConnections.Remove(gitHub.Id);
        Assert(store.AppConnections.GetToken(gitHub.Id) is null, "disconnecting an app removes its Vault credential");
        var appConnectionsPage = new silaa.UI.AppConnectionsPage
        {
            DataContext = new silaa.ViewModels.AppConnectionsViewModel(
                store, navigation, () => Task.FromResult<string?>(documentRoot),
                (_, _) => Task.FromResult(true), (_, _, _) => { })
        };
        Assert(appConnectionsPage.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "앱 연결"),
            "connected apps page loads for GitHub and OneDrive setup");
        var appConnectionsRoot = (Grid)appConnectionsPage.Content!;
        Assert(appConnectionsRoot.ColumnDefinitions[0].Width.IsAuto,
            "connected apps page sidebar uses an Auto GridLength and cannot trigger the same navigation crash");
        var openAppConnections = typeof(silaa.UI.MainWindow).GetMethod(
            "OpenAppConnections", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("OpenAppConnections method not found");
        openAppConnections.Invoke(window, null);
        Assert(navigationPageHost.Children.SingleOrDefault() is silaa.UI.AppConnectionsPage,
            "the related MainWindow app-connections navigation path also attaches without crashing");
        var skills = new silaa.UI.SkillsPage { DataContext = new silaa.ViewModels.SkillsViewModel(store, navigation) };
        var vault = new silaa.UI.VaultPage
        {
            DataContext = new silaa.ViewModels.VaultViewModel(store, navigation,
                (_, _) => Task.FromResult(false), (_, _, _) => { })
        };
        var vaultViewModel = (silaa.ViewModels.VaultViewModel)vault.DataContext!;
        var vaultDialog = vault.FindControl<Border>("VaultDialogOverlay")
            ?? throw new Exception("VaultDialogOverlay not found");
        var vaultSecretInput = vault.FindControl<TextBox>("VaultSecretInput")
            ?? throw new Exception("VaultSecretInput not found");
        Assert(!vaultDialog.IsVisible && vaultSecretInput.PasswordChar == '●',
            "Vault secret dialog starts closed with masked input");
        vaultViewModel.OpenAddCommand.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert(vaultDialog.IsVisible, "Vault key add command opens the dedicated secret dialog");
        vaultViewModel.DraftName = "DIALOG_TEST_TOKEN";
        vaultViewModel.DraftSecret = "dialog-secret";
        vaultViewModel.RevealSecret = true;
        Assert(vaultViewModel.SecretPasswordChar == '\0',
            "Vault dialog can explicitly reveal and re-mask the current draft");
        vaultViewModel.CancelCommand.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert(!vaultDialog.IsVisible && vaultViewModel.DraftName.Length == 0 &&
               vaultViewModel.DraftSecret.Length == 0 && !vaultViewModel.RevealSecret,
            "cancelling Vault input clears the secret draft from the view model");
        var memory = new silaa.UI.MemoryPage
        {
            DataContext = new silaa.ViewModels.MemoryViewModel(harness, null, null, navigation,
                (_, _) => Task.FromResult(false), (_, _, _) => { })
        };
        Assert(memory.GetLogicalDescendants().OfType<silaa.UI.MainMenuView>().Any() &&
               vault.GetLogicalDescendants().OfType<silaa.UI.SettingsMenuView>().Any(),
            "Memory is a workspace-level page while Vault remains inside the settings sidebar shell");
        var automation = new silaa.UI.AutomationPage
        {
            DataContext = new silaa.ViewModels.AutomationViewModel(harness, navigation,
                (_, _) => Task.FromResult(false), _ => Task.CompletedTask)
        };
        var mySchedule = new silaa.UI.MySchedulePage
        {
            DataContext = new silaa.ViewModels.MyScheduleViewModel(harness, navigation, () => Task.CompletedTask)
        };
        var browser = new silaa.UI.BrowserPage(navigation, null, () => { }, (_, _) => { });
        Assert(silaa.UI.SilaaBrand.ApplicationIcon() is not null,
            "Windows application icon is available for native tray registration");
        Assert(settings.Content is not null && home.Content is not null && window.Content is not null &&
               skills.Content is not null && vault.Content is not null && memory.Content is not null &&
                automation.Content is not null && mySchedule.Content is not null && browser.Content is not null,
            "compiled AXAML views load with their runtime resources");
        var messageList = window.FindControl<StackPanel>("MessageList")
            ?? throw new Exception("MessageList not found");
        var messageScroll = window.FindControl<ScrollViewer>("MessageScroll")
            ?? throw new Exception("MessageScroll not found");
        Assert(messageList.HorizontalAlignment == Avalonia.Layout.HorizontalAlignment.Stretch &&
               messageScroll.HorizontalContentAlignment == Avalonia.Layout.HorizontalAlignment.Stretch,
            "initial transcript keeps a full-width message column for left and right alignment");
        var stopButton = window.FindControl<Button>("StopButton")
            ?? throw new Exception("StopButton not found");
        var sendButton = window.FindControl<Button>("SendButton")
            ?? throw new Exception("SendButton not found");
        Assert(sendButton.HorizontalAlignment == Avalonia.Layout.HorizontalAlignment.Right &&
               sendButton.VerticalAlignment == Avalonia.Layout.VerticalAlignment.Center,
            "composer send button stays right-aligned and vertically centered");
        Assert(window.FindControl<Button>("ResponseStatusButton") is not null &&
               window.FindControl<Border>("ResponseSessionsPopup") is not null,
            "global status bar exposes the responding-session popup");
        var reasoningSelector = window.FindControl<ComboBox>("ReasoningEffortSelector")
            ?? throw new Exception("ReasoningEffortSelector not found");
        Assert(reasoningSelector.Items.Count == 6 && reasoningSelector.Height == 21,
            "status bar exposes a compact six-level reasoning selector next to response state");
        var windowViewModel = (silaa.ViewModels.MainWindowViewModel)window.DataContext!;
        windowViewModel.IsRunning = true;
        Assert(stopButton.IsVisible && windowViewModel.SendLabel == "추가 보내기" && windowViewModel.CanStartSession,
            "composer keeps send available, exposes stop, and allows a new conversation while another responds");
        windowViewModel.IsRunning = false;

        var setWorkflowView = typeof(silaa.UI.MainWindow).GetMethod(
            "SetWorkflowView", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("MainWindow workflow view method not found");
        var surveyExitButton = window.FindControl<Button>("SurveyExitButton")
            ?? throw new Exception("SurveyExitButton not found");
        var mainDialogHost = window.FindControl<silaa.UI.SiDialogHost>("DialogHost")
            ?? throw new Exception("DialogHost not found");
        setWorkflowView.Invoke(window, [true]);
        surveyExitButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert(mainDialogHost.IsVisible && surveyExitButton.IsVisible,
            "leaving a survey first opens a confirmation dialog without discarding the workflow");
        var keepSurvey = mainDialogHost.GetLogicalDescendants().OfType<Button>()
            .First(button => Equals(button.Content, "취소"));
        keepSurvey.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert(!mainDialogHost.IsVisible && surveyExitButton.IsVisible,
            "cancelling the exit confirmation keeps the survey open");
        setWorkflowView.Invoke(window, [false]);

        var selectedSessionField = typeof(silaa.UI.MainWindow).GetField(
            "_sessionId", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("MainWindow selected session field not found");
        var activeRunSessionField = typeof(silaa.UI.MainWindow).GetField(
            "_activeRunSessionId", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("MainWindow active run session field not found");
        var renderRunEvent = typeof(silaa.UI.MainWindow).GetMethod(
            "RenderRunEvent", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("MainWindow RenderRunEvent method not found");
        selectedSessionField.SetValue(window, null);
        activeRunSessionField.SetValue(window, null);
        windowViewModel.IsViewingActiveRun = false;
        renderRunEvent.Invoke(window, [new ChatRunEvent("status", "streaming", "ses_background"), null, null]);
        Assert(selectedSessionField.GetValue(window) is null &&
               (string?)activeRunSessionField.GetValue(window) == "ses_background" &&
               !windowViewModel.IsViewingActiveRun,
            "a late stream event cannot pull a newly opened conversation back to the responding session");
        activeRunSessionField.SetValue(window, null);
        windowViewModel.IsViewingActiveRun = true;

        var runCancellationField = typeof(silaa.UI.MainWindow).GetField(
            "_runCancellation", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("MainWindow run state field not found");
        using var simulatedRun = new CancellationTokenSource();
        runCancellationField.SetValue(window, simulatedRun);
        ((silaa.ViewModels.MainWindowViewModel)window.DataContext!).OpenSettingsCommand.Execute(null);
        var pageHost = window.FindControl<Grid>("PageHost")
            ?? throw new Exception("PageHost not found");
        Assert(pageHost.Children.FirstOrDefault() is silaa.UI.SettingsPage,
            "LLM connection page opens even while an Agent run is active");
        runCancellationField.SetValue(window, null);

        window.RequestExit();
        Console.WriteLine("silaa AXAML smoke check passed");
    }
    finally
    {
        if (Directory.Exists(xamlRoot)) Directory.Delete(xamlRoot, true);
    }
    return;
}

if (args.Contains("--layout-snapshots", StringComparer.OrdinalIgnoreCase))
{
    AppBuilder.Configure<silaa.SilaaApplication>().UsePlatformDetect().SetupWithoutStarting();
    var outputRoot = args.SkipWhile(arg => !arg.Equals("--layout-snapshots", StringComparison.OrdinalIgnoreCase))
        .Skip(1).FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "silaa-layout-snapshots");
    outputRoot = Path.GetFullPath(outputRoot);
    Directory.CreateDirectory(outputRoot);
    var dataRoot = Path.Combine(Path.GetTempPath(), "silaa-layout-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dataRoot);
    try
    {
        var store = new LocalStore(dataRoot);
        store.SaveSettings("http://127.0.0.1:11434/v1", "snapshot-model", dataRoot);
        await using var harness = new LlmHarness(store);
        silaa.ViewModels.NavigationViewModel Navigation(silaa.ViewModels.NavigationSection section) => new(
            () => Task.CompletedTask, () => Task.CompletedTask, () => { }, () => { }, () => { }, () => { }, () => { },
            activeSection: section);
        var homeViewModel = new silaa.ViewModels.HomePageViewModel(
            harness, Navigation(silaa.ViewModels.NavigationSection.Home), _ => Task.CompletedTask, () => Task.CompletedTask, _ => Task.CompletedTask,
            () => Task.CompletedTask, () => { }, () => { }, () => { }, () => { }, () => { });
        homeViewModel.Apps.Add(new silaa.ViewModels.ProjectCardViewModel(new ProjectInfo
        {
            Id = "snapshot-app", Name = "업무 대시보드", Directory = dataRoot,
            Summary = "팀 업무를 한눈에 확인하는 내부 앱",
            UpdatedAt = DateTimeOffset.UtcNow,
            Delivery = new ProjectDeliveryProfile
            {
                Kind = "webapp", SkillName = "web-app-delivery", AppPort = 5100, InternalPort = 3000
            }
        }, _ => Task.CompletedTask));
        homeViewModel.HasApps = true;
        var home = new silaa.UI.HomePage { DataContext = homeViewModel };
        var appsViewModel = new silaa.ViewModels.HomePageViewModel(
            harness, Navigation(silaa.ViewModels.NavigationSection.Apps), _ => Task.CompletedTask, () => Task.CompletedTask, _ => Task.CompletedTask,
            () => Task.CompletedTask, () => { }, () => { }, () => { }, () => { }, () => { }, appsOnly: true);
        appsViewModel.Projects.Add(homeViewModel.Apps[0]);
        appsViewModel.HasProjects = true;
        appsViewModel.IsEmpty = false;
        var apps = new silaa.UI.HomePage { DataContext = appsViewModel };
        var memory = new silaa.UI.MemoryPage
        {
            DataContext = new silaa.ViewModels.MemoryViewModel(
                harness, null, null, Navigation(silaa.ViewModels.NavigationSection.Memory), (_, _) => Task.FromResult(false), (_, _, _) => { })
        };
        var vault = new silaa.UI.VaultPage
        {
            DataContext = new silaa.ViewModels.VaultViewModel(
                store, Navigation(silaa.ViewModels.NavigationSection.Vault), (_, _) => Task.FromResult(false), (_, _, _) => { })
        };
        var vaultDialogViewModel = new silaa.ViewModels.VaultViewModel(
            store, Navigation(silaa.ViewModels.NavigationSection.Vault), (_, _) => Task.FromResult(false), (_, _, _) => { });
        var vaultDialog = new silaa.UI.VaultPage { DataContext = vaultDialogViewModel };
        vaultDialogViewModel.OpenAddCommand.Execute(null);
        var skills = new silaa.UI.SkillsPage
        {
            DataContext = new silaa.ViewModels.SkillsViewModel(store, Navigation(silaa.ViewModels.NavigationSection.Skills))
        };
        var automation = new silaa.UI.AutomationPage
        {
            DataContext = new silaa.ViewModels.AutomationViewModel(
                harness, Navigation(silaa.ViewModels.NavigationSection.Automation), (_, _) => Task.FromResult(false), _ => Task.CompletedTask)
        };
        var snapshotEventStart = DateTimeOffset.Now.AddDays(1).Date.AddHours(14);
        store.Calendar.Add(
            "제품 리뷰 미팅", snapshotEventStart.ToString("O"), snapshotEventStart.AddHours(1).ToString("O"),
            false, "회의실 A", "결정 사항과 다음 액션 정리", reminderTaskId: "task_snapshot_reminder");
        var myScheduleViewModel = new silaa.ViewModels.MyScheduleViewModel(
            harness, Navigation(silaa.ViewModels.NavigationSection.Schedule), () => Task.CompletedTask);
        await myScheduleViewModel.RefreshAsync();
        var mySchedule = new silaa.UI.MySchedulePage { DataContext = myScheduleViewModel };
        var presentationCatalog = new silaa.UI.PresentationTemplatesPage
        {
            DataContext = new silaa.ViewModels.PresentationTemplatesViewModel(
                Navigation(silaa.ViewModels.NavigationSection.PresentationTemplates),
                new DocumentTemplateStore(dataRoot),
                _ => Task.FromResult(DocumentTemplateStore.BuiltInOrDefault("pptx", "silaa-mono")))
        };
        var settings = new silaa.UI.SettingsPage(store, harness, Navigation(silaa.ViewModels.NavigationSection.Settings));
        var settingsEditor = new silaa.UI.SettingsPage(store, harness, Navigation(silaa.ViewModels.NavigationSection.Settings));
        ((silaa.ViewModels.SettingsViewModel)settingsEditor.DataContext!).NewConnectionCommand.Execute(null);
        var browser = new silaa.UI.BrowserPage(
            Navigation(silaa.ViewModels.NavigationSection.Browser), null, () => { }, (_, _) => { }, true);
        var chat = new silaa.UI.MainWindow(store, harness);
        chat.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var pageHost = chat.FindControl<Grid>("PageHost") ?? throw new InvalidOperationException("PageHost not found");
        var chatShell = chat.FindControl<Grid>("ChatShell") ?? throw new InvalidOperationException("ChatShell not found");
        var dropOverlay = chat.FindControl<Border>("DropOverlay") ?? throw new InvalidOperationException("DropOverlay not found");
        pageHost.Children.Clear();
        pageHost.Children.Add(chatShell);
        ((silaa.ViewModels.MainWindowViewModel)chat.DataContext!).NewSessionCommand.Execute(null);

        var snapshots = new (string Name, Control View)[]
        {
            ("01-home.png", home),
            ("01b-apps.png", apps),
            ("02-chat.png", chat),
            ("03-memory.png", memory),
            ("04-vault.png", vault),
            ("04b-vault-dialog.png", vaultDialog),
            ("05-skills.png", skills),
            ("06-automation.png", automation),
            ("06a-my-schedule.png", mySchedule),
            ("06b-presentation-templates.png", presentationCatalog),
            ("07-browser.png", browser),
            ("08-settings.png", settings),
            ("09-settings-editor.png", settingsEditor)
        };
        foreach (var (name, view) in snapshots)
        {
            var path = Path.Combine(outputRoot, name);
            SaveLayoutSnapshot(view, path);
            Console.WriteLine(path);
            if (name == "02-chat.png")
            {
                dropOverlay.IsVisible = true;
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                var dropPath = Path.Combine(outputRoot, "02b-chat-file-drop-dark.png");
                SaveLayoutSnapshot(chat, dropPath);
                Console.WriteLine(dropPath);
                dropOverlay.IsVisible = false;
            }
            if (name == "06b-presentation-templates.png")
            {
                ((silaa.ViewModels.PresentationTemplatesViewModel)presentationCatalog.DataContext!)
                    .Templates.First(card => card.Id == "silaa-mono").SelectCommand.Execute(null);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                var editorPath = Path.Combine(outputRoot, "06c-template-live-editor.png");
                SaveLayoutSnapshot(presentationCatalog, editorPath);
                Console.WriteLine(editorPath);
            }
        }
        chat.RequestExit();
    }
    finally
    {
        if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
    }
    return;
}

if (args.Contains("--ollama-harness-probe", StringComparer.OrdinalIgnoreCase))
{
    var probeRoot = Path.Combine(Path.GetTempPath(), "silaa-ollama-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(probeRoot);
    try
    {
        var store = new LocalStore(probeRoot);
        store.SaveSettings("http://127.0.0.1:11434/v1", "qwen3.8:27b", probeRoot,
            maxOutputTokens: 256, requestTimeoutSeconds: 180, reasoningEffort: "high");
        await using var harness = new LlmHarness(store);
        var events = new List<ChatRunEvent>();
        var result = await harness.RunAsync(null, "17 * 23을 계산하고 답만 짧게 말해줘.", events.Add);
        Console.WriteLine($"reasoning_events={events.Count(item => item.Kind == "reasoning")}, reasoning_chars={events.Where(item => item.Kind == "reasoning").Sum(item => item.Text.Length)}, answer={result.Answer}");
    }
    finally
    {
        if (Directory.Exists(probeRoot)) Directory.Delete(probeRoot, true);
    }
    return;
}

var shellCommandCount = 0;
var shellViewModel = new silaa.ViewModels.MainWindowViewModel(
    () => { shellCommandCount++; return Task.CompletedTask; },
    () => shellCommandCount++,
    () => shellCommandCount++,
    () => { shellCommandCount++; return Task.CompletedTask; },
    () => { shellCommandCount++; return Task.CompletedTask; },
    () => { shellCommandCount++; return Task.CompletedTask; },
    () => { shellCommandCount++; return Task.CompletedTask; },
    () => shellCommandCount++,
    () => { shellCommandCount++; return Task.CompletedTask; },
    () => { shellCommandCount++; return Task.CompletedTask; },
    () => shellCommandCount++);
shellViewModel.EditorText = "MVVM binding";
shellViewModel.IsRunning = true;
await shellViewModel.SendOrCancelCommand.ExecuteAsync(null);
shellViewModel.StopCommand.Execute(null);
shellViewModel.OpenSettingsCommand.Execute(null);
Assert(shellViewModel.EditorText == "MVVM binding" && shellViewModel.SendLabel == "추가 보내기" && shellCommandCount == 3,
    "CommunityToolkit shell view model separates queued send and stop commands");
shellViewModel.IsRunning = false;
Assert(shellViewModel.SendLabel == "보내기", "run state updates the bound send label");
shellViewModel.IsRunning = true;
shellViewModel.PendingPromptCount = 2;
Assert(shellViewModel.HasPendingPrompts && shellViewModel.PendingPromptLabel == "대기 2개" &&
       shellViewModel.ComposerHint.Contains("추가 지시"),
    "composer view model exposes queued steering status");
shellViewModel.IsViewingActiveRun = false;
Assert(shellViewModel.CanCompose && shellViewModel.ComposerHint.Contains("다른 세션") &&
       shellViewModel.ComposerPlaceholder.Contains("병렬 전송") && shellViewModel.SendLabel == "병렬 보내기",
    "browsing another session during a run advertises immediate parallel sending");
shellViewModel.IsViewingActiveRun = true;
shellViewModel.IsParallelSessionRun = true;
Assert(!shellViewModel.CanCompose && shellViewModel.SendLabel == "응답 중" &&
       shellViewModel.ComposerHint.Contains("다른 세션에서는 동시에"),
    "the responding parallel session is read-only while other sessions remain available");
shellViewModel.IsParallelSessionRun = false;
shellViewModel.IsRunning = false;

var parallelSendGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var parallelSendCount = 0;
var parallelCommandViewModel = new silaa.ViewModels.MainWindowViewModel(
    async () =>
    {
        parallelSendCount++;
        await parallelSendGate.Task;
    },
    () => { }, () => { },
    () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask,
    () => Task.CompletedTask, () => { }, () => Task.CompletedTask,
    () => Task.CompletedTask, () => { });
var firstSend = parallelCommandViewModel.SendOrCancelCommand.ExecuteAsync(null);
await Task.Yield();
Assert(parallelCommandViewModel.SendOrCancelCommand.CanExecute(null),
    "the send command stays enabled while the first conversation is awaiting a response");
var firstParallelSend = parallelCommandViewModel.SendOrCancelCommand.ExecuteAsync(null);
await Task.Yield();
Assert(parallelSendCount == 2,
    "the first parallel conversation can send immediately through the same async command");
parallelSendGate.SetResult();
await Task.WhenAll(firstSend, firstParallelSend);

var steeringQueue = new silaa.UI.ConversationSteeringQueue();
steeringQueue.Start();
steeringQueue.BeginPrompt();
Assert(steeringQueue.Enqueue("first follow-up") == 1 && steeringQueue.ShouldContinueAfterCancellation,
    "mid-response follow-up requests a steering transition");
Assert(steeringQueue.TryTakeNext(out var queuedPrompt) && queuedPrompt == "first follow-up" &&
       steeringQueue.PendingCount == 0,
    "steering queue preserves follow-up order");
steeringQueue.Enqueue("restore me");
Assert(steeringQueue.RequestStop().SequenceEqual(["restore me"]) && steeringQueue.StopRequested,
    "explicit stop returns unsent queued prompts to the editor");
Assert(steeringQueue.Finish().Count == 0 && !steeringQueue.IsActive,
    "steering queue resets after the conversation loop finishes");

var copiedConversation = silaa.UI.MainWindow.FormatConversationForCopy("Nemotron 최신 모델",
    [
        new ChatMessage { Role = "user", Text = "최신 모델을 찾아줘" },
        new ChatMessage { Role = "assistant", Text = "공식 자료를 확인했습니다." }
    ], "조회 결과를 정리하는 중입니다.");
var normalizedConversation = copiedConversation.Replace("\r\n", "\n");
Assert(normalizedConversation.Contains("# Nemotron 최신 모델") &&
       normalizedConversation.Contains("## 나\n\n최신 모델을 찾아줘") &&
       normalizedConversation.Contains("## silaa\n\n조회 결과를 정리하는 중입니다."),
    "whole conversation copy preserves roles and includes the live streamed answer");
var legacyAttachmentPrompt = "첨부한 파일을 읽어줘.\n\n첨부 파일:\n- C:\\workspace\\.silaa\\attachments\\Avalonia.Base 3.dll\n  처리 지침: 바이너리 파일: inspect_file_metadata만 사용하세요.";
var visibleAttachmentPrompt = AttachmentService.UserVisiblePrompt(legacyAttachmentPrompt);
Assert(visibleAttachmentPrompt.Contains("- Avalonia.Base 3.dll") &&
       !visibleAttachmentPrompt.Contains("처리 지침") && !visibleAttachmentPrompt.Contains("C:\\workspace"),
    "legacy attachment prompts hide internal paths and agent-only handling instructions");

var streamingMarkdown = new silaa.UI.MarkdownView();
streamingMarkdown.Append("# Streaming Markdown\n\n**selectable** while streaming");
streamingMarkdown.RefreshStreamingForTest();
Assert(streamingMarkdown.Root.Children.OfType<Avalonia.Controls.StackPanel>()
       .SelectMany(panel => panel.Children).OfType<Avalonia.Controls.SelectableTextBlock>().Any(),
    "streaming output is incrementally rendered as Markdown blocks");
streamingMarkdown.Complete();
var completedSelectable = streamingMarkdown.Root.Children.OfType<Avalonia.Controls.SelectableTextBlock>().FirstOrDefault();
Assert(completedSelectable is not null,
    "completed Markdown preserves selectable structured blocks");
Assert(ReferenceEquals(completedSelectable!.SelectionForegroundBrush, silaa.UI.Palette.OnAccent),
    "Markdown selection keeps readable foreground contrast in light and dark themes");
Assert(silaa.UI.MainWindow.ShouldFollowMessageTail(970, 1600, 600) &&
       !silaa.UI.MainWindow.ShouldFollowMessageTail(300, 1600, 600),
    "streaming follows the tail only while the reader remains near the bottom");
var initialOpenHandled = false;
Assert(silaa.UI.MainWindow.TryBeginInitialOpen(ref initialOpenHandled) &&
       !silaa.UI.MainWindow.TryBeginInitialOpen(ref initialOpenHandled),
    "window content initialization runs only once across tray hide and restore");
Assert(silaa.UI.MainWindow.RestorableWindowState(WindowState.Minimized) == WindowState.Normal &&
       silaa.UI.MainWindow.RestorableWindowState(WindowState.Maximized) == WindowState.Maximized,
    "tray restore preserves the last usable window state");
var richMarkdown = new silaa.UI.MarkdownView(
    "| Name | State |\n| --- | --- |\n| Build | Ready |\n\n> quoted context\n\n- [x] verified\n\n```csharp\nConsole.WriteLine(\"done\");\n```");
var richSelectionSurfaces = richMarkdown.Root.Children.OfType<Avalonia.Controls.SelectableTextBlock>().ToList();
var richSelectableText = string.Concat(richSelectionSurfaces
    .SelectMany(block => block.Inlines ?? [])
    .OfType<Avalonia.Controls.Documents.Run>()
    .Select(run => run.Text));
Assert(richSelectionSurfaces.Count == 1 &&
       richSelectableText.Contains("| Name | State |") &&
       richSelectableText.Contains("| Build | Ready |"),
    "completed Markdown keeps tables and surrounding content in one scroll-selectable copy surface");
Assert(!richMarkdown.Root.GetLogicalDescendants().OfType<Grid>()
       .Any(grid => grid.ColumnDefinitions.Count == 2 && grid.RowDefinitions.Count == 2),
    "completed tables no longer split selection into one control per cell");
var chartSource = """
{"type":"bar","title":"Quarterly revenue","unit":"M","labels":["Q1","Q2","Q3"],"series":[{"name":"Revenue","values":[12,18,24]}]}
""";
Assert(silaa.UI.ChartHtmlRenderer.TryBuild(chartSource, out var chartHtml, out var chartSummary, out var chartKind, out _),
    "valid chart JSON builds self-contained HTML");
Assert(chartKind == "bar" && chartHtml.Contains("Content-Security-Policy") && chartHtml.Contains("<svg") &&
       !chartHtml.Contains("https://") && chartSummary.Contains("Q3 24M"),
    "chart HTML is offline, accessible, and preserves values");
var injectionSource = """
{"type":"line","title":"</script><script>alert(1)</script>","labels":["A"],"series":[{"name":"Value","values":[1]}]}
""";
Assert(silaa.UI.ChartHtmlRenderer.TryBuild(injectionSource, out var safeChartHtml, out _, out var safeChartKind, out _) &&
       safeChartKind == "line" && !safeChartHtml.Contains("</script><script>alert(1)</script>"),
    "chart text cannot break out of the isolated JSON payload");
Assert(!silaa.UI.ChartHtmlRenderer.TryBuild(
        "{\"type\":\"donut\",\"labels\":[\"A\"],\"series\":[{\"name\":\"Bad\",\"values\":[-1]}]}",
        out _, out _, out _, out _),
    "invalid donut data is rejected");
var chartMarkdown = new silaa.UI.MarkdownView($"```chart\n{chartSource}\n```");
Assert(chartMarkdown.Root.Children.OfType<silaa.UI.HtmlChartView>().Any(),
    "Markdown chart fence uses the HTML chart surface");
var weekdayCron = silaa.Services.CronSchedule.Parse("*/15 9-17 * * 1-5");
var mondayMorning = new DateTimeOffset(2026, 11, 2, 8, 59, 0, TimeSpan.Zero);
Assert(weekdayCron.GetNext(mondayMorning, TimeZoneInfo.Utc) ==
       new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero),
    "cron steps, ranges, and weekdays calculate the next local occurrence");
AssertThrows<ArgumentException>(() => silaa.Services.CronSchedule.Parse("0 9 * *"),
    "cron requires five fields");
Assert(LlmHarness.EffectiveReasoningEffort("high", "calendar") == "low" &&
       LlmHarness.EffectiveReasoningEffort("high", "schedule") == "high" &&
       LlmHarness.EffectiveReasoningEffort("medium", null) == "medium",
    "calendar registration uses short low reasoning without changing normal or recurring-task requests");
using (var currentTime = JsonDocument.Parse(silaa.Services.DateTimeTools.GetCurrentDateTime()))
{
    var rootElement = currentTime.RootElement;
    Assert(rootElement.GetProperty("local_datetime").GetString()!.Contains('T') &&
           rootElement.GetProperty("utc_datetime").GetString()!.EndsWith('Z') &&
           rootElement.GetProperty("time_zone_id").GetString()!.Length > 0 &&
           rootElement.GetProperty("day_of_week_ko").GetString()!.EndsWith("요일"),
        "datetime tool returns structured local, UTC, offset, weekday, and time-zone data");
}
var calendarRoot = Path.Combine(Path.GetTempPath(), "silaa-calendar-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(calendarRoot);
try
{
    var legacyReminderId = "task_legacy_calendar";
    var legacyDueAt = DateTimeOffset.UtcNow.AddDays(2);
    File.WriteAllText(Path.Combine(calendarRoot, "background-automation.json"), JsonSerializer.Serialize(new
    {
        Tasks = new[]
        {
            new
            {
                Id = legacyReminderId, Name = "서류 제출 마감 알림", Instruction = "오전 10시까지 제출",
                Kind = "reminder", Status = "waiting", DueAt = legacyDueAt
            }
        },
        Schedules = Array.Empty<object>()
    }));
    var calendar = new silaa.Services.CalendarEventStore(calendarRoot);
    var migratedEvent = calendar.List().Single();
    Assert(migratedEvent.Title == "서류 제출 마감" && migratedEvent.ReminderTaskId == legacyReminderId &&
           migratedEvent.StartAt == legacyDueAt,
        "legacy waiting reminders migrate once into real calendar events without importing cron jobs");
    var createdEvent = calendar.Add(
        "제품 리뷰 미팅", "2026-11-12T14:00:00+09:00", "2026-11-12T15:00:00+09:00",
        false, "회의실 A", "결정 사항 정리");
    Assert(createdEvent.Id.StartsWith("event_") && createdEvent.ReminderTaskId is null &&
           createdEvent.PreparationTaskId is null,
        "calendar event is stored independently without silently creating an alarm or Agent task");
    var reopenedCalendar = new silaa.Services.CalendarEventStore(calendarRoot);
    var persistedEvent = reopenedCalendar.List().Single(item => item.Id == createdEvent.Id);
    Assert(persistedEvent.Title == "제품 리뷰 미팅" && persistedEvent.Location == "회의실 A" &&
           persistedEvent.StartAt == createdEvent.StartAt,
        "real calendar events survive restart with their date, time, location, and notes");
    Assert(reopenedCalendar.Delete(createdEvent.Id) && reopenedCalendar.List().Count == 1 &&
           reopenedCalendar.Delete(migratedEvent.Id) && reopenedCalendar.List().Count == 0,
        "calendar events can be deleted without touching automation state");
}
finally
{
    Directory.Delete(calendarRoot, true);
}
var automationRoot = Path.Combine(Path.GetTempPath(), "silaa-automation-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(automationRoot);
try
{
    var finished = new TaskCompletionSource<silaa.Core.BackgroundTaskInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
    var reminderFinished = new TaskCompletionSource<silaa.Core.BackgroundTaskInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
    var agentRuns = 0;
    var automation = new silaa.Services.BackgroundAutomationService(automationRoot,
        (task, _) =>
        {
            agentRuns++;
            return Task.FromResult(new silaa.Core.ChatRunResult("ses_background", "verified result"));
        });
        var onboarding = new AutomationOnboardingTools(
            "task", "flow_reminder_guard", null, new ProjectInteractionBroker(), automation, null);
        var finalizeReminder = typeof(AutomationOnboardingTools).GetMethod(
            "CreateReminder", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("CreateReminder onboarding tool not found");
        var reminderGuarded = false;
        try
        {
            finalizeReminder.Invoke(onboarding, ["알림", "내용", 60]);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is InvalidOperationException)
        {
            reminderGuarded = true;
        }
        Assert(reminderGuarded,
            "reminder onboarding cannot finalize before asking what notification content to show");
    try
    {
        automation.TaskChanged += task =>
        {
            if (task.Status == "completed") finished.TrySetResult(task);
            if (task.Status == "completed" && task.Kind == "reminder") reminderFinished.TrySetResult(task);
        };
        var queued = automation.StartTask("Run focused verification.", null, "Verification");
        var completed = finished.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        Assert(completed.Id == queued.Id && completed.SessionId == "ses_background" &&
               completed.ResultPreview == "verified result", "background queue persists its completed result session");
        var reminder = automation.CreateReminder("Local reminder delivered.", 60, "Reminder probe");
        Assert(reminder.Status == "waiting" && reminder.Kind == "reminder" && reminder.DueAt > DateTimeOffset.UtcNow,
            "one-time reminder persists without occupying an LLM request");
        automation.TriggerDueReminders(reminder.DueAt);
        var delivered = reminderFinished.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        Assert(delivered.ResultPreview == "Local reminder delivered." && delivered.SessionId is null && agentRuns == 1,
            "due reminder completes locally without invoking the Agent runner");
        var schedule = automation.CreateSchedule("Five minute check", "*/5 * * * *", "Run the check.", null);
        Assert(schedule.Enabled && schedule.NextRunAt > DateTimeOffset.UtcNow, "cron job is registered with a future run");
        automation.TriggerDueSchedules(schedule.NextRunAt);
        for (var attempt = 0; attempt < 100 && !automation.ListTasks().Any(task => task.ScheduleId == schedule.Id && task.Status == "completed"); attempt++)
            Thread.Sleep(10);
        Assert(automation.ListTasks().Any(task => task.ScheduleId == schedule.Id && task.Status == "completed"),
            "due cron occurrence queues and completes a background task");
        automation.SetScheduleEnabled(schedule.Id, false);
        Assert(!automation.ListSchedules().Single().Enabled, "cron job can be paused");
    }
    finally { automation.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    var reopenedAutomation = new silaa.Services.BackgroundAutomationService(automationRoot,
        (task, _) => Task.FromResult(new silaa.Core.ChatRunResult("ses_reopened", "ok")));
    try
    {
        Assert(reopenedAutomation.ListTasks().Any(task => task.Status == "completed") &&
               reopenedAutomation.ListSchedules().Single().Enabled == false,
            "background tasks and cron schedules survive restart");
    }
    finally { reopenedAutomation.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
}
finally
{
    Directory.Delete(automationRoot, true);
}
var dialogHost = new silaa.UI.SiDialogHost();
var pendingConfirmation = dialogHost.ConfirmAsync("Delete", "Confirm deletion", "Delete", danger: true);
Assert(dialogHost.IsVisible && !pendingConfirmation.IsCompleted && dialogHost.Children.Count == 1,
    "confirmation dialog blocks for an explicit user decision");
var pendingSecretInput = dialogHost.PromptSecretAsync(
    "Vault key", "Needed for the requested integration.", "INTEGRATION_TOKEN");
var secureInputs = dialogHost.GetLogicalDescendants().OfType<TextBox>().ToList();
Assert(pendingConfirmation.IsCompleted && !await pendingConfirmation &&
       dialogHost.IsVisible && !pendingSecretInput.IsCompleted && secureInputs.Count == 2 &&
       secureInputs[1].PasswordChar == '●',
    "secure Vault prompt replaces another dialog and masks the secret value");
var secretCancel = dialogHost.GetLogicalDescendants().OfType<Button>()
    .First(button => Equals(button.Content, "취소"));
secretCancel.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
Assert(await pendingSecretInput is null && !dialogHost.IsVisible,
    "cancelling secure Vault input returns no secret material");
var interactionBroker = new silaa.Services.ProjectInteractionBroker();
ChatRunEvent? interactionEvent = null;
var interactionTask = interactionBroker.RequestAsync(
    "session", "어느 방식으로 진행할까요?", "빠르게|꼼꼼하게", "직접 입력", 0,
    "선택이 필요합니다", item => interactionEvent = item, CancellationToken.None, showProgress: false);
var interactionRequest = JsonSerializer.Deserialize<ProjectInputRequest>(interactionEvent!.Text)!;
Assert(interactionEvent.Kind == "input_request" && !interactionRequest.ShowProgress &&
       interactionRequest.Options.SequenceEqual(["빠르게", "꼼꼼하게"]),
    "ordinary conversation choice tool emits a native card without survey progress chrome");
Assert(interactionBroker.Submit(interactionRequest.RequestId, "빠르게") &&
       await interactionTask == "빠르게",
    "native choice card resumes the waiting Agent tool with the selected answer");
var toastHost = new silaa.UI.SiToastHost();
toastHost.Show("Saved", "The operation completed.", silaa.UI.SiStatusTone.Success, 60_000);
Assert(toastHost.Children.Count == 1,
    "toast host presents transient operation feedback");
var accessibleInput = new Avalonia.Controls.TextBox();
_ = new silaa.UI.SiField("Project title", "Used by the agent.", accessibleInput);
Assert(Avalonia.Automation.AutomationProperties.GetName(accessibleInput) == "Project title" &&
       Avalonia.Automation.AutomationProperties.GetHelpText(accessibleInput) == "Used by the agent.",
    "form fields expose accessible names and help text");
var narrowHeader = new silaa.UI.SiPageHeader("context", "A long project title that must wrap in a narrow inspector", "Description");
var responsiveTitle = narrowHeader.Children.OfType<Avalonia.Controls.StackPanel>()
    .SelectMany(panel => panel.Children).OfType<Avalonia.Controls.TextBlock>()
    .First(block => block.Text == "A long project title that must wrap in a narrow inspector");
Assert(responsiveTitle.TextWrapping == Avalonia.Media.TextWrapping.Wrap,
    "page headers wrap long titles inside a narrow responsive inspector");

if (args.Contains("--ui-latency-probe", StringComparer.OrdinalIgnoreCase))
{
    AppBuilder.Configure<silaa.SilaaApplication>().UsePlatformDetect().SetupWithoutStarting();
    var timeline = new silaa.UI.AgentRunView();
    var samples = new List<double>();
    var total = System.Diagnostics.Stopwatch.StartNew();
    for (var index = 0; index < 40; index++)
    {
        var frame = System.Diagnostics.Stopwatch.StartNew();
        timeline.AppendReasoning($"Inspecting phase {index}. " + new string('r', 300));
        timeline.FlushStreamingPreviews();
        timeline.BeginTool($"probe-{index}", $"search_text\n{{\"query\":\"item-{index}\"}}");
        timeline.CompleteTool($"probe-{index}", "search_text\n" + new string('x', 1_000));
        timeline.AppendAnswer($"## Result {index}\n\n- rendered **in order**\n- value `{index}`\n\n");
        timeline.FlushStreamingPreviews();
        timeline.Root.Measure(new Avalonia.Size(840, 100_000));
        timeline.Root.Arrange(new Avalonia.Rect(0, 0, 840, Math.Max(1, timeline.Root.DesiredSize.Height)));
        frame.Stop();
        samples.Add(frame.Elapsed.TotalMilliseconds);
    }
    timeline.Complete();
    timeline.Root.Measure(new Avalonia.Size(840, 100_000));
    timeline.Root.Arrange(new Avalonia.Rect(0, 0, 840, Math.Max(1, timeline.Root.DesiredSize.Height)));
    total.Stop();
    samples.Sort();
    var median = samples[samples.Count / 2];
    var p95 = samples[(int)Math.Ceiling(samples.Count * 0.95) - 1];
    Console.WriteLine($"UI latency: median={median:0.00} ms, p95={p95:0.00} ms, max={samples[^1]:0.00} ms, total={total.ElapsedMilliseconds} ms for 40 phases");
    return;
}

if (args.Contains("--api-latency-probe", StringComparer.OrdinalIgnoreCase))
{
    var current = new LocalStore();
    if (!current.IsConfigured) throw new Exception("silaa is not configured.");
    using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(90)
    };
    var key = current.GetApiKey();
    if (!string.IsNullOrWhiteSpace(key))
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
    var uri = new Uri(EndpointGuard.Parse(current.Settings.LlmEndpoint), "chat/completions");
    var json = JsonSerializer.Serialize(new
    {
        model = current.Settings.Model,
        max_tokens = 256,
        stream = true,
        temperature = 0.2,
        messages = new[]
        {
            new { role = "user", content = "Explain UI batching in exactly five short sentences. Do not call tools." }
        }
    });
    using var request = new HttpRequestMessage(HttpMethod.Post, uri)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
    var watch = System.Diagnostics.Stopwatch.StartNew();
    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    var headersMs = watch.Elapsed.TotalMilliseconds;
    response.EnsureSuccessStatusCode();
    await using var stream = await response.Content.ReadAsStreamAsync();
    using var reader = new StreamReader(stream);
    double? firstEventMs = null;
    double? firstTokenMs = null;
    var eventCount = 0;
    var outputCharacters = 0;
    while (await reader.ReadLineAsync() is { } line)
    {
        if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
        var data = line[5..].Trim();
        if (data.Length == 0) continue;
        firstEventMs ??= watch.Elapsed.TotalMilliseconds;
        if (data == "[DONE]") break;
        eventCount++;
        using var document = JsonDocument.Parse(data);
        if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
        if (!choices[0].TryGetProperty("delta", out var delta)) continue;
        foreach (var field in new[] { "reasoning", "reasoning_content", "content" })
        {
            if (!delta.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String) continue;
            var text = value.GetString();
            if (string.IsNullOrEmpty(text)) continue;
            firstTokenMs ??= watch.Elapsed.TotalMilliseconds;
            outputCharacters += text.Length;
        }
    }
    watch.Stop();
    Console.WriteLine($"API latency ({uri.Host}, {current.Settings.Model}): headers={headersMs:0} ms, first_event={firstEventMs:0} ms, first_token={firstTokenMs:0} ms, total={watch.Elapsed.TotalMilliseconds:0} ms, events={eventCount}, chars={outputCharacters}");
    return;
}

if (args.Contains("--render-bench", StringComparer.OrdinalIgnoreCase))
{
    var markdown = new silaa.UI.MarkdownView();
    var timeline = new silaa.UI.AgentRunView();
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    for (var index = 0; index < 10_000; index++)
    {
        markdown.Append("stream-token ");
        timeline.AppendReasoning("reasoning-token ");
    }
    markdown.Append("\n\n```csharp\nConsole.WriteLine(\"done\");\n```\n");
    markdown.Complete();
    timeline.BeginTool("benchmark", "read_file\n{\"path\":\"large.cs\"}");
    timeline.CompleteTool("benchmark", "read_file\n" + new string('x', 100_000));
    timeline.AppendAnswer("done");
    timeline.Complete();
    var incremental = new silaa.UI.MarkdownView();
    for (var index = 0; index < 300; index++)
    {
        incremental.Append($"## Block {index}\n\nparagraph **{index}**\n\n");
        incremental.RefreshStreamingForTest();
    }
    incremental.Complete();
    stopwatch.Stop();
    Console.WriteLine($"Rendered 20,002 chunks, 300 forced Markdown refreshes, and a 100k tool result in {stopwatch.ElapsedMilliseconds} ms");
    return;
}

var timelineView = new silaa.UI.AgentRunView();
Assert(timelineView.IsWaiting, "new assistant run displays a waiting spinner before the first event");
timelineView.AppendReasoning("reasoning one");
Assert(!timelineView.IsWaiting, "waiting spinner stops when the first reasoning event arrives");
timelineView.BeginTool("call-1", "read_file\n{\"path\":\"a.cs\"}");
timelineView.CompleteTool("call-1", "read_file\ncontents");
timelineView.AppendReasoning("reasoning two");
timelineView.BeginTool("call-2", "list_files\n{\"path\":\".\"}");
timelineView.BeginTool("call-3", "run_command\n{\"command\":\"dotnet test\"}");
timelineView.AppendAnswer("done");
timelineView.Complete();
Assert(string.Join(',', timelineView.TimelineKinds) == "reasoning,tool,reasoning,tool,tool,answer",
    "agent activity preserves chronological timeline");

var interleavedTimeline = new silaa.UI.AgentRunView();
interleavedTimeline.AppendAnswer("before tool");
interleavedTimeline.BeginTool("mixed-1", "search_text\n{}");
interleavedTimeline.CompleteTool("mixed-1", "search_text\ndone");
interleavedTimeline.AppendAnswer("after tool");
interleavedTimeline.Complete();
Assert(string.Join(',', interleavedTimeline.TimelineKinds) == "answer,tool,answer",
    "text phases stay in event order around tool activity");

var splitMultipartView = new silaa.UI.AgentRunView();
splitMultipartView.AppendAnswer("[{'type': 'text', 'text': ''}, {'type': 'te");
splitMultipartView.AppendAnswer("xt', 'text': '\\n\\n좋아요. 개인용 루틴 트래커로 가는 것 같네요.'}]");
var splitMultipartFinal = LlmHarness.NormalizeTextParts(splitMultipartView.AnswerText).Trim();
splitMultipartView.SetFinalAnswer(splitMultipartFinal);
Assert(splitMultipartView.AnswerText == "좋아요. 개인용 루틴 트래커로 가는 것 같네요." &&
       string.Join(',', splitMultipartView.TimelineKinds) == "answer",
    "final normalized answer replaces a multipart literal that was already painted from split stream chunks");

var permissionTimeline = new silaa.UI.AgentRunView();
permissionTimeline.BeginTool("permission-1", "request_folder_access\n{}");
permissionTimeline.AddInteractive(new Border(), "permission");
permissionTimeline.CompleteTool("permission-1", "request_folder_access\napproved");
permissionTimeline.AppendAnswer("continued");
permissionTimeline.Complete();
Assert(string.Join(',', permissionTimeline.TimelineKinds) == "tool,permission,answer",
    "interactive permission cards remain in chronological tool and answer order");

if (args.Contains("--apply-nvidia-sample", StringComparer.OrdinalIgnoreCase))
{
    var current = new LocalStore();
    current.SaveSettings(
        current.Settings.LlmEndpoint, current.Settings.Model, current.Settings.WorkspacePath,
        apiKey: "", maxOutputTokens: 1024, contextWindowTokens: current.Settings.ContextWindowTokens,
        maxTokensField: "max_tokens", temperature: 0.5, topP: 1,
        requestTimeoutSeconds: 120, frequencyPenalty: 0, presencePenalty: 0, seed: 0);
    Console.WriteLine("Applied NVIDIA-compatible sampling settings.");
    return;
}

if (args.Any(value => value.Equals("--raw", StringComparison.OrdinalIgnoreCase) || value.Equals("--raw-stream", StringComparison.OrdinalIgnoreCase)))
{
    var current = new LocalStore();
    using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(45)
    };
    var key = current.GetApiKey();
    if (!string.IsNullOrWhiteSpace(key)) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    var uri = new Uri(EndpointGuard.Parse(current.Settings.LlmEndpoint), "chat/completions");
    var modelOverride = args.FirstOrDefault(value => value.StartsWith("--model=", StringComparison.OrdinalIgnoreCase));
    var probeModel = modelOverride is null ? current.Settings.Model : modelOverride["--model=".Length..];
    var streaming = args.Contains("--raw-stream", StringComparer.OrdinalIgnoreCase);
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(streaming ? "text/event-stream" : "application/json"));
    var json = JsonSerializer.Serialize(new
    {
        model = probeModel,
        max_tokens = 1024,
        stream = streaming,
        temperature = 0.5,
        top_p = 1,
        frequency_penalty = 0,
        presence_penalty = 0,
        seed = 0,
        messages = new[]
        {
            new { role = "system", content = "You are a helpful assistant." },
            new { role = "user", content = "What is 17 * 23?" }
        }
    });
    using var request = new HttpRequestMessage(HttpMethod.Post, uri)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    var body = await response.Content.ReadAsStringAsync();
    Console.WriteLine($"Raw HTTP status: {(int)response.StatusCode}");
    Console.WriteLine(body.Length > 2000 ? body[..2000] : body);
    return;
}

if (args.Contains("--live", StringComparer.OrdinalIgnoreCase))
{
    var current = new LocalStore();
    if (!current.IsConfigured) throw new Exception("silaa is not configured.");
    var probeRoot = Path.Combine(Path.GetTempPath(), "silaa-live-probe-" + Guid.NewGuid().ToString("N"));
    try
    {
        var probe = new LocalStore(probeRoot);
        probe.SaveSettings(
            current.Settings.LlmEndpoint, current.Settings.Model, current.Settings.WorkspacePath,
            apiKey: current.GetApiKey(), requestTimeoutSeconds: 60);
        await using var harness = new LlmHarness(probe);
        var promptOverride = args.FirstOrDefault(value => value.StartsWith("--prompt=", StringComparison.OrdinalIgnoreCase));
        var livePrompt = promptOverride is null ? "Reply with only OK. Do not call tools." : promptOverride["--prompt=".Length..];
        var liveEvents = new List<silaa.Core.ChatRunEvent>();
        var response = await harness.RunAsync(null, livePrompt, liveEvents.Add);
        foreach (var group in liveEvents.GroupBy(item => item.Kind))
            Console.WriteLine($"Live event {group.Key}: {group.Count()}");
        Console.WriteLine($"Live Agent Framework response: {response.Answer}");
    }
    finally
    {
        if (Directory.Exists(probeRoot)) Directory.Delete(probeRoot, true);
    }
    return;
}

var endpoint = EndpointGuard.Parse("http://127.0.0.1:11434/v1");
Assert(endpoint.AbsoluteUri == "http://127.0.0.1:11434/v1/", "endpoint normalization");
try
{
    EndpointGuard.Parse("http://localhost:11434/v1/chat/completions");
    throw new Exception("completion path accepted");
}
catch (ArgumentException) { }

if (OperatingSystem.IsWindows())
{
    var migrationRoot = Path.Combine(Path.GetTempPath(), "silaa-settings-migration-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(migrationRoot);
    try
    {
        File.WriteAllText(Path.Combine(migrationRoot, "settings.json"), JsonSerializer.Serialize(new
        {
            LlmEndpoint = "http://127.0.0.1:11434/v1/",
            Model = "legacy-model",
            WorkspacePath = migrationRoot,
            MaxOutputTokens = 2048,
            ReasoningEffort = "high"
        }), Encoding.UTF8);
        var migratedStore = new LocalStore(migrationRoot);
        Assert(migratedStore.LlmConnections.Count == 1 &&
               migratedStore.ActiveConnection is { Name: "기본 연결", Model: "legacy-model" } &&
               migratedStore.Settings.MaxOutputTokens == 2048 && migratedStore.Settings.ReasoningEffort == "high",
            "legacy single LLM settings migrate to an active connection profile");
    }
    finally
    {
        Directory.Delete(migrationRoot, true);
    }

    var root = Path.Combine(Path.GetTempPath(), "silaa-test-" + Guid.NewGuid().ToString("N"));
    var workspace = Path.Combine(root, "workspace");
    Directory.CreateDirectory(workspace);
    File.WriteAllText(Path.Combine(workspace, "probe.txt"), "native tool result", Encoding.UTF8);
    var sampleProject = Path.Combine(workspace, "sample-app");
    Directory.CreateDirectory(sampleProject);
    File.WriteAllText(Path.Combine(sampleProject, "package.json"), "{}", Encoding.UTF8);
    var globalAccess = Path.Combine(root, "global-access");
    var projectAccess = Path.Combine(root, "project-access");
    var sessionAccess = Path.Combine(root, "session-access");
    var deniedAccess = Path.Combine(root, "denied-access");
    foreach (var directory in new[] { globalAccess, projectAccess, sessionAccess, deniedAccess })
        Directory.CreateDirectory(directory);
    File.WriteAllText(Path.Combine(globalAccess, "global.txt"), "global allowed", Encoding.UTF8);
    File.WriteAllText(Path.Combine(deniedAccess, "blocked.txt"), "approval granted", Encoding.UTF8);
    try
    {
        await using (var partialServer = new MockOpenAiServer(streamPartialUntilCancelled: true))
        {
            var partialRoot = Path.Combine(root, "partial-store");
            var partialStore = new LocalStore(partialRoot);
            partialStore.SaveSettings(partialServer.Endpoint, "partial-model", workspace, requestTimeoutSeconds: 30);
            await using var partialHarness = new LlmHarness(partialStore);
            using var partialCancellation = new CancellationTokenSource();
            var wasCancelled = false;
            try
            {
                await partialHarness.RunAsync(null, "stream a partial answer", item =>
                {
                    if (item.Kind == "text") partialCancellation.Cancel();
                }, partialCancellation.Token);
            }
            catch (OperationCanceledException) { wasCancelled = true; }
            var partialSession = (await partialHarness.ListSessionsAsync()).Single();
            var partialMessages = await partialHarness.LoadMessagesAsync(partialSession.Id);
            Assert(wasCancelled && partialMessages.Count == 2 && partialMessages[1].Role == "assistant" &&
                   partialMessages[1].Text == "partial answer",
                "cancelled streaming persists the partial assistant answer for steering continuity");
        }

        await using (var childServer = new MockOpenAiServer(directAnswer: "delegated evidence"))
        {
            var client = new OpenAIClient(
                new ApiKeyCredential("child-test"),
                new OpenAIClientOptions { Endpoint = new Uri(childServer.Endpoint) });
            using var childChatClient = client.GetChatClient("child-model").AsIChatClient();
            using var childTimeout = new RunActivityTimeout(
                CancellationToken.None, TimeSpan.FromSeconds(5));
            var childEvents = new List<ChatRunEvent>();
            var multiAgentTools = new MultiAgentTools(
                childChatClient,
                new AppSettings { MaxOutputTokens = 1024 },
                "ses_multi",
                "Implement and verify a complex change.",
                [],
                [],
                childTimeout,
                childEvents.Add);
            var delegated = await multiAgentTools.DelegateTaskAsync(
                "implementer", "Inspect the target and report a bounded implementation.", "Relevant test context.",
                childTimeout.Token);
            var reviewed = await multiAgentTools.ReviewResultAsync(
                delegated, "correctness and regression risk", childTimeout.Token);
            Assert(delegated == "delegated evidence" && reviewed == "delegated evidence",
                "worker and reviewer child Agents return their results to the lead Agent");
            Assert(childEvents.Count(item => item.Kind == "agent_call") == 2 &&
                   childEvents.Count(item => item.Kind == "agent_result") == 2 &&
                   childEvents.All(item => item.SessionId == "ses_multi"),
                "child Agent lifecycle events remain attached to the parent session");
            var childRequests = childServer.Requests.ToArray();
            Assert(childRequests.Length == 2,
                "delegated worker and independent reviewer each run through Microsoft Agent Framework");
            using var workerRequest = JsonDocument.Parse(childRequests[0].Body);
            using var reviewRequest = JsonDocument.Parse(childRequests[1].Body);
            Assert((workerRequest.RootElement.GetProperty("messages")[0].GetProperty("content").GetString() ?? "")
                       .Contains("focused implementer child Agent", StringComparison.Ordinal) &&
                   (reviewRequest.RootElement.GetProperty("messages")[0].GetProperty("content").GetString() ?? "")
                       .Contains("independent read-only reviewer Agent", StringComparison.Ordinal),
                "worker specialization and reviewer read-only boundaries are sent as system instructions");
        }

        await using var server = new MockOpenAiServer();
        var store = new LocalStore(root);
        store.SaveSettings(
            server.Endpoint, "model", workspace, apiKey: "secret-value",
            maxOutputTokens: 321, contextWindowTokens: 131_072,
            maxTokensField: "max_tokens", temperature: 0.2, topP: 0.8,
            requestTimeoutSeconds: 30, frequencyPenalty: 0, presencePenalty: 0, seed: 0,
            reasoningEffort: "high", allowedDirectories: [globalAccess]);

        var reopened = new LocalStore(root);
        Assert(reopened.GetApiKey() == "secret-value", "DPAPI credential round-trip");
        Assert(reopened.HasApiKey, "stored API key status");
        Assert(reopened.Settings.GlobalAllowedDirectories.SequenceEqual([globalAccess]),
            "global folder permission round-trip");
        Assert(reopened.SaveSettings(
            server.Endpoint, "gemma4:31b", workspace, apiKey: "", maxOutputTokens: 321,
            contextWindowTokens: 131_072, maxTokensField: "max_tokens",
            temperature: 0.2, topP: 0.8, requestTimeoutSeconds: 30,
            frequencyPenalty: 0, presencePenalty: 0, seed: 0, theme: "light"),
            "theme settings preserve API key");
        var themed = new LocalStore(root);
        Assert(themed.Settings.Theme == "light", "theme setting round-trip");
        Assert(themed.Settings.ReasoningEffort == "high", "reasoning effort setting round-trip");
        Assert(themed.SetActiveReasoningEffort("low").ReasoningEffort == "low" &&
               new LocalStore(root).ActiveConnection?.ReasoningEffort == "low",
            "status-bar reasoning changes persist immediately on the active LLM connection");
        themed.SetActiveReasoningEffort("high");
        var workspaceBeforeConnectionEdit = themed.Settings.WorkspacePath;
        var directoriesBeforeConnectionEdit = themed.Settings.GlobalAllowedDirectories.ToArray();
        var endpointsBeforeConnectionEdit = themed.Settings.GlobalAllowedNetworkEndpoints.ToArray();
        var themeBeforeConnectionEdit = themed.Settings.Theme;
        var activeConnectionBeforeEdit = themed.ActiveConnection
            ?? throw new Exception("active LLM connection missing");
        themed.SaveLlmConnection(
            activeConnectionBeforeEdit.Id,
            activeConnectionBeforeEdit.Name,
            activeConnectionBeforeEdit.LlmEndpoint,
            activeConnectionBeforeEdit.Model,
            maxOutputTokens: 333,
            reasoningEffort: activeConnectionBeforeEdit.ReasoningEffort);
        Assert(themed.Settings.WorkspacePath == workspaceBeforeConnectionEdit &&
               themed.Settings.GlobalAllowedDirectories.SequenceEqual(directoriesBeforeConnectionEdit) &&
               themed.Settings.GlobalAllowedNetworkEndpoints.SequenceEqual(endpointsBeforeConnectionEdit) &&
               themed.Settings.Theme == themeBeforeConnectionEdit,
            "LLM connection save is independent from workspace, permission, and appearance settings");
        var profileBeforeWorkspaceEdit = themed.ActiveConnection
            ?? throw new Exception("active LLM connection missing after edit");
        themed.SaveWorkspaceSettings(
            workspace,
            "dark",
            [globalAccess],
            ["api.example.com:443"]);
        var profileAfterWorkspaceEdit = themed.ActiveConnection
            ?? throw new Exception("active LLM connection missing after workspace save");
        Assert(profileAfterWorkspaceEdit.Id == profileBeforeWorkspaceEdit.Id &&
               profileAfterWorkspaceEdit.LlmEndpoint == profileBeforeWorkspaceEdit.LlmEndpoint &&
               profileAfterWorkspaceEdit.Model == profileBeforeWorkspaceEdit.Model &&
               profileAfterWorkspaceEdit.MaxOutputTokens == profileBeforeWorkspaceEdit.MaxOutputTokens &&
               themed.Settings.GlobalAllowedNetworkEndpoints.SequenceEqual(["api.example.com:443"]),
            "workspace and Agent security save does not mutate the active LLM profile");
        AssertThrows<ArgumentException>(() => themed.SaveSettings(
            server.Endpoint, "model", workspace, theme: "sepia"), "invalid theme rejected");
        Assert(reopened.Settings.MaxOutputTokens == 321 && reopened.Settings.ContextWindowTokens == 131_072, "token settings round-trip");
        Assert(reopened.Settings.Temperature == 0.2 && reopened.Settings.TopP == 0.8, "sampling settings round-trip");
        Assert(reopened.Settings.FrequencyPenalty == 0 && reopened.Settings.PresencePenalty == 0 && reopened.Settings.Seed == 0,
            "additional sampling settings round-trip");
        var primaryConnection = reopened.ActiveConnection
            ?? throw new Exception("primary LLM connection was not created");
        Assert(reopened.LlmConnections.Count == 1 && primaryConnection.Model == "gemma4:31b",
            "legacy single settings are represented as the first connection profile");
        Assert(reopened.SaveConnection(
                null, "Secondary", server.Endpoint, "secondary-model", workspace,
                apiKey: "second-secret", maxOutputTokens: 512, contextWindowTokens: 65_536,
                maxTokensField: "max_completion_tokens", reasoningEffort: "medium"),
            "second connection stores its own API key");
        var secondaryConnection = reopened.ActiveConnection
            ?? throw new Exception("secondary LLM connection was not activated");
        Assert(reopened.LlmConnections.Count == 2 && secondaryConnection.Name == "Secondary" &&
               reopened.GetApiKey() == "second-secret", "multiple LLM profiles can be saved and selected");
        var multiReopened = new LocalStore(root);
        Assert(multiReopened.LlmConnections.Count == 2 && multiReopened.GetApiKey() == "second-secret",
            "multiple profiles and active selection survive restart");
        reopened.ActivateConnection(primaryConnection.Id);
        Assert(reopened.Settings.Model == "gemma4:31b" && reopened.GetApiKey() == "secret-value",
            "activating a profile restores its model options and independent API key");
        Assert(reopened.DeleteConnection(secondaryConnection.Id) && reopened.LlmConnections.Count == 1 &&
               reopened.GetApiKey() == "secret-value", "deleting one profile preserves another profile credential");
        Assert(!File.ReadAllText(Path.Combine(root, "settings.json")).Contains("secret-value"), "settings credential leak");
        Assert(!File.ReadAllText(Path.Combine(root, "settings.json")).Contains("second-secret"), "secondary settings credential leak");
        Assert(!File.ReadAllText(Path.Combine(root, "llm-credential.json")).Contains("secret-value"), "unencrypted credential");
        reopened.Vault.Save("PROBE_TOKEN", "vault-secret-value");
        Assert(reopened.Vault.Get("PROBE_TOKEN") == "vault-secret-value", "Vault DPAPI round-trip");
        Assert(!File.ReadAllText(Path.Combine(root, "vault.json")).Contains("vault-secret-value"), "Vault plaintext leak");
        var vaultTools = new WorkspaceTools(workspace, reopened.Vault);
        var vaultOutput = await vaultTools.RunCommandWithVaultAsync("Write-Output $env:PROBE_TOKEN", "PROBE_TOKEN", "PROBE_TOKEN", 10);
        Assert(vaultOutput.Contains("[VAULT_SECRET]") && !vaultOutput.Contains("vault-secret-value"), "Vault command output redaction");
        Environment.SetEnvironmentVariable("SILAA_TEST_SECRET", "must-not-leak");
        var sanitizedStart = new System.Diagnostics.ProcessStartInfo();
        WorkspaceTools.ConfigureSanitizedEnvironment(sanitizedStart);
        Assert(!sanitizedStart.Environment.ContainsKey("SILAA_TEST_SECRET") &&
               sanitizedStart.Environment["SILAA_SECURITY_MODE"] == "enforce",
            "command processes receive a minimal environment without ambient secrets");
        Environment.SetEnvironmentVariable("SILAA_TEST_SECRET", null);
        var securityEndpoints = new List<string> { "api.example.com:443" };
        var security = new SecurityPolicyContext("ses_security", root, securityEndpoints, reopened.SecurityAudit);
        security.ApplyNetworkEnvironment(sanitizedStart);
        Assert(sanitizedStart.Environment["HTTPS_PROXY"] == "http://127.0.0.1:9" &&
               sanitizedStart.Environment["NO_PROXY"] == "api.example.com",
            "common CLI network traffic receives a deny proxy with only approved hosts bypassed");
        Assert(!security.InspectCommand("curl https://blocked.example/data").Allowed &&
               security.InspectCommand("curl https://api.example.com/data").Allowed,
            "network policy denies unknown endpoints and permits exact approved endpoints");
        Assert(!security.InspectCommand("powershell -EncodedCommand ZgBvAG8=").Allowed,
            "process policy blocks nested encoded shells");
        Assert(!security.InspectCommand("Get-Content vault.json").Allowed,
            "command policy blocks direct references to protected credential state");
        Assert(!security.InspectCommand("curl https://api.example.com/data", credentialPresent: true).Allowed &&
               security.InspectCommand("curl https://api.example.com/data", "api.example.com:443", true).Allowed,
            "Vault credentials require an independent exact endpoint binding");
        AssertThrows<UnauthorizedAccessException>(() =>
                security.EnsurePathAllowed(Path.Combine(root, "vault.json"), false),
            "security policy protects Vault state even when a workspace parent is accessible");
        var securedTools = new WorkspaceTools(workspace, reopened.Vault, reopened.Settings.GlobalAllowedDirectories, security);
        Assert((await securedTools.RunCommandAsync("curl https://blocked.example/data", 10)).StartsWith("NETWORK_DENIED:"),
            "workspace command tool returns a structured network denial before process launch");
        Assert((await securedTools.RunCommandAsync("Write-Output local", 10)).Contains("Exit code: 0"),
            "local commands continue through the enforce policy");
        var scopedTools = new WorkspaceTools(workspace, reopened.Vault, reopened.Settings.GlobalAllowedDirectories);
        Assert(scopedTools.ListAllowedDirectories().Contains(globalAccess) &&
               scopedTools.ListAllowedDirectories().Contains(workspace),
            "file tools expose their effective folder boundaries");
        var binaryProbePath = Path.Combine(workspace, "silaa-probe.dll");
        File.Copy(typeof(LlmHarness).Assembly.Location, binaryProbePath);
        AssertThrows<InvalidOperationException>(() => scopedTools.ReadFile("silaa-probe.dll"),
            "binary files are rejected by the UTF-8 text reader");
        var binaryMetadata = scopedTools.InspectFileMetadata("silaa-probe.dll");
        Assert(binaryMetadata.Contains("portable-executable") &&
               binaryMetadata.Contains("hasClrMetadata") && binaryMetadata.Contains("assemblyName"),
            "binary metadata inspection reports verified PE and CLR facts");
        Assert(AttachmentService.AgentReadInstruction(binaryProbePath).Contains("inspect_file_metadata") &&
               !AttachmentService.AgentReadInstruction(binaryProbePath).Contains("read_file을 사용하세요"),
            "binary attachments direct the agent to metadata inspection instead of text decoding");
        Assert(scopedTools.GlobFiles("**/*.txt").Contains("probe.txt") &&
               scopedTools.GlobFiles("*.txt", globalAccess).Contains("global.txt"),
            "glob tool searches recursively in workspace and explicitly allowed roots");
        var originalProbe = File.ReadAllText(Path.Combine(workspace, "probe.txt"));
        var editResult = scopedTools.ApplyEdits([
            new WorkspaceTextEdit { Path = "probe.txt", OldText = "native tool result", NewText = "staged edit" },
            new WorkspaceTextEdit { Path = "probe.txt", OldText = "staged edit", NewText = "native tool result" }
        ]);
        Assert(editResult.Contains("2 edit(s)") && File.ReadAllText(Path.Combine(workspace, "probe.txt")) == originalProbe,
            "coordinated edits validate and stage sequential replacements before writing");
        AssertThrows<InvalidOperationException>(() => scopedTools.ApplyEdits([
                new WorkspaceTextEdit { Path = "probe.txt", OldText = "native tool result", NewText = "should not persist" },
                new WorkspaceTextEdit { Path = "probe.txt", OldText = "missing block", NewText = "never" }
            ]), "invalid coordinated edit is rejected");
        Assert(File.ReadAllText(Path.Combine(workspace, "probe.txt")) == originalProbe,
            "failed coordinated edit leaves every file untouched");
        var skillTools = new SkillManagementTools(workspace);
        Assert(skillTools.ListLocalSkills() == "No local skills found.",
            "local skill catalog starts empty");
        var createdSkill = skillTools.CreateOrUpdateSkill(
            "release-checks",
            "Verify release artifacts when the user asks for a distributable build.",
            "# Release checks\n\n- Build the requested configuration.\n- Report the exact artifact path.",
            false);
        Assert(createdSkill.Contains("is valid") && skillTools.ValidateSkill("release-checks").Contains("is valid") &&
               skillTools.ListLocalSkills().Contains("release-checks"),
            "skill creator saves, validates, and lists a project-local skill");
        AssertThrows<InvalidOperationException>(() => skillTools.CreateOrUpdateSkill(
                "release-checks", "Duplicate.", "# Duplicate", false),
            "skill creator requires explicit overwrite for updates");
        AssertThrows<ArgumentException>(() => skillTools.CreateOrUpdateSkill(
                "Invalid Name", "Invalid.", "# Invalid", false),
            "skill creator rejects unsafe skill names");
        Assert(SkillLoader.BuildSystemPrompt(workspace).Contains("Release checks"),
            "new project-local skill loads into the next Agent system prompt");
        var architectureSkill = SkillLoader.ReadSkill("architecture-diagram", workspace, root);
        Assert(architectureSkill.Contains("evidence-based software architecture diagrams") &&
               architectureSkill.Contains("Prefer Mermaid"),
            "bundled architecture-diagram skill is discoverable by the local harness");
        var bundledDeliverySkill = SkillLoader.ReadSkill("web-app-delivery", workspace, root);
        Assert(bundledDeliverySkill.Contains("Next.js server exclusively") &&
               bundledDeliverySkill.Contains("External access uses ngrok"),
            "editable web-app delivery skill owns the default stack and external exposure policy");
        var overrideWorkspace = Path.Combine(root, "skill-override-workspace");
        var overrideSkillDirectory = Path.Combine(overrideWorkspace, ".silaa", "skills", "web-app-delivery");
        Directory.CreateDirectory(overrideSkillDirectory);
        File.WriteAllText(Path.Combine(overrideSkillDirectory, "SKILL.md"),
            "# Custom web stack\n\nUse the project-selected replacement stack.\n", Encoding.UTF8);
        Assert(SkillLoader.ReadSkill("web-app-delivery", overrideWorkspace, root).Contains("Custom web stack"),
            "project-local delivery skill overrides the bundled stack without changing C# code");
        var deliveryRoot = Path.Combine(root, "delivery-store");
        var deliveryWorkspace = Path.Combine(root, "delivery-workspace");
        Directory.CreateDirectory(deliveryRoot);
        Directory.CreateDirectory(deliveryWorkspace);
        var deliveryStore = new ProjectStore(deliveryRoot);
        var deliveredProject = deliveryStore.Create(deliveryWorkspace, "web-output", "웹 앱", 80, [], "app");
        Assert(deliveredProject.Delivery.SkillName == "web-app-delivery" &&
               deliveredProject.Delivery.AppPort == 5100 && deliveredProject.Delivery.InternalPort == 3000,
            "app projects persist only the editable delivery skill reference and assigned ports");
        var importSource = Path.Combine(workspace, "import-source");
        Directory.CreateDirectory(Path.Combine(importSource, "references"));
        File.WriteAllText(Path.Combine(importSource, "SKILL.md"),
            "---\nname: lab-helper\ndescription: \"Guide one practice lab.\"\n---\n\n# Lab helper\n\nFollow the lab source.\n",
            Encoding.UTF8);
        File.WriteAllText(Path.Combine(importSource, "references", "checklist.md"), "# Checklist\n", Encoding.UTF8);
        var installer = new SkillInstallerService(workspace, root);
        var installedSkill = await installer.InstallAsync(importSource, "", "project", false);
        Assert(installedSkill.Name == "lab-helper" && installedSkill.FileCount == 2 &&
               File.Exists(Path.Combine(workspace, ".silaa", "skills", "lab-helper", "references", "checklist.md")),
            "skill installer validates and atomically copies a complete local package");
        await AssertThrowsAsync<InvalidOperationException>(
            () => installer.InstallAsync(importSource, "", "project", false),
            "skill installer refuses implicit replacement");
        await AssertThrowsAsync<ArgumentException>(
            () => installer.InstallAsync(importSource, "../escape", "project", false),
            "skill installer rejects repository subpath traversal");
        var globalSkill = await installer.InstallAsync(importSource, "", "global", false);
        Assert(globalSkill.Scope == "global" &&
               SkillLoader.BuildSystemPrompt(workspace, null, root).Contains("Follow the lab source."),
            "global Skill packages load into Agent instructions across projects");
        var gitSetup = await scopedTools.RunCommandAsync(
            "git init --quiet; git add probe.txt; git -c user.name=silaa -c user.email=silaa@local commit -m initial --quiet", 20);
        Assert(gitSetup.Contains("Exit code: 0") && scopedTools.GitStatus().StartsWith("##", StringComparison.Ordinal),
            "structured Git status runs directly in the primary workspace");
        File.AppendAllText(Path.Combine(workspace, "probe.txt"), "\ngit diff probe", Encoding.UTF8);
        Assert(scopedTools.GitDiff("working", "probe.txt").Contains("+git diff probe"),
            "structured Git diff can be limited to one workspace path");
        File.WriteAllText(Path.Combine(workspace, "probe.txt"), originalProbe, Encoding.UTF8);
        var temporaryGit = Path.Combine(workspace, ".git");
        foreach (var file in Directory.EnumerateFiles(temporaryGit, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(temporaryGit, true);
        Assert(scopedTools.ReadFile(Path.Combine(globalAccess, "global.txt")).Contains("global allowed"),
            "file tools can read an explicitly allowed global folder");
        AssertThrows<UnauthorizedAccessException>(() => scopedTools.ReadFile(Path.Combine(deniedAccess, "blocked.txt")),
            "file tools reject paths outside the workspace and allowed folders");

        var permissionRoot = Path.Combine(root, "permission-store");
        Directory.CreateDirectory(permissionRoot);

        using (var activityTimeout = new RunActivityTimeout(
                   CancellationToken.None, TimeSpan.FromMilliseconds(200)))
        {
            using (activityTimeout.Pause())
            {
                await Task.Delay(350);
                Assert(!activityTimeout.Token.IsCancellationRequested,
                    "interactive user waits do not consume the Agent inactivity timeout");
            }

            await AssertThrowsAsync<OperationCanceledException>(
                () => Task.Delay(TimeSpan.FromSeconds(2), activityTimeout.Token),
                "Agent inactivity timeout resumes after interactive input completes");
            Assert(activityTimeout.TimedOut,
                "inactivity cancellation is identified separately from user cancellation");
        }

        var permissionStore = new LocalStore(permissionRoot);
        permissionStore.SaveSettings(server.Endpoint, "model", workspace);
        var permissionSessions = new SessionStore(permissionRoot);
        var permissionSessionId = permissionSessions.Create("Permission probe", workspace);
        var draftSessionId = permissionSessions.Create("Hidden setup", workspace, isDraft: true);
        Assert(permissionSessions.IsDraft(draftSessionId) &&
               permissionSessions.List(workspace).All(session => session.Id != draftSessionId),
            "setup drafts stay out of every visible conversation list");
        permissionSessions.MoveToProject(draftSessionId, "project_probe");
        Assert(!permissionSessions.IsDraft(draftSessionId) &&
               permissionSessions.List(workspace, "project_probe").Any(session => session.Id == draftSessionId),
            "promoting a setup draft makes it visible only as a project session");
        var disposableDraftId = permissionSessions.Create("Discard setup", workspace, isDraft: true);
        Assert(permissionSessions.DiscardDraft(disposableDraftId) && permissionSessions.Get(disposableDraftId) is null,
            "cancelled setup drafts are removed without leaving stale history");
        var surveyProject = new ProjectInfo
        {
            Id = "prj_survey_history",
            Name = "루틴 트래커",
            Directory = workspace,
            Summary = "개인 루틴을 체크하고 달성률을 시각화하는 앱",
            SpecificityScore = 82,
            DiscoveryHistory =
            [
                new ProjectDiscoveryEntry
                {
                    Question = "어떤 문제를 해결할까요?", Answer = "매일 루틴을 빠뜨리지 않게 해주세요.",
                    SpecificityScore = 45, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2)
                },
                new ProjectDiscoveryEntry
                {
                    Question = "어떤 결과를 보고 싶나요?", Answer = "달성률과 연속 기록을 보고 싶어요.",
                    SpecificityScore = 82, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
                }
            ]
        };
        var surveySessionId = permissionSessions.CreateProjectSurveySession(surveyProject, workspace);
        var surveySessions = permissionSessions.List(workspace, surveyProject.Id);
        var surveyMessages = permissionSessions.Messages(surveySessionId);
        Assert(surveySessions.Count == 1 && surveySessions[0].Title == "루틴 트래커 기획 설문" &&
               surveyMessages.Count == 5 && surveyMessages[0].Role == "assistant" &&
               surveyMessages[1].Role == "user" && surveyMessages[^1].Text.Contains("구체화 82%"),
            "a completed project survey becomes the first visible project conversation with its Q&A history");
        var deletedSessionId = permissionSessions.Create("삭제할 대화", workspace);
        permissionSessions.Append(deletedSessionId, new ChatMessage
        {
            Role = "user", Text = "삭제 대상", CreatedAt = DateTimeOffset.UtcNow
        });
        Assert(permissionSessions.Delete(deletedSessionId) && permissionSessions.Get(deletedSessionId) is null &&
               permissionSessions.Messages(deletedSessionId).Count == 0,
            "deleting a conversation removes the session and all of its messages");
        Assert(LlmHarness.NormalizeTextParts(
                   "[{'type': 'text', 'text': ''}, {'type': 'text', 'text': '\\n\\n정상 응답'}]").Trim() == "정상 응답" &&
               LlmHarness.NormalizeTextParts(
                   "[{\"type\":\"text\",\"text\":\"\"},{\"type\":\"text\",\"text\":\"hello\"}]") == "hello",
            "multipart text responses discard empty parts and render only joined text");
        const string longMultipart = "[{'type': 'text', 'text': ''}, {'type': 'text', 'text': '\\n\\n좋아요. 앱에서 직접 입력·체크하고, 결과를 시각화해서 보는 개인용 루틴 트래커로 가는 것 같네요.\\n\\n- 접근 방식: 웹앱 vs 모바일 웹\\n- 핵심 화면: 루틴 목록/체크\\n'}]";
        var normalizedLongMultipart = LlmHarness.NormalizeTextParts(longMultipart);
        Assert(normalizedLongMultipart.Contains("개인용 루틴 트래커") &&
               normalizedLongMultipart.Contains("핵심 화면") && !normalizedLongMultipart.Contains("'type'"),
            "long Korean multipart literals normalize without leaking Python list syntax");
        var embeddedParts = LlmHarness.NormalizeTextParts(
            "앞 문장\n[{'type': 'text', 'text': ''}, {'type': 'text', 'text': '\\n본문'}]\n뒤 문장");
        Assert(embeddedParts.Contains("앞 문장") && embeddedParts.Contains("본문") && embeddedParts.Contains("뒤 문장") &&
               !embeddedParts.Contains("'type'"),
            "stored answers also clean embedded multipart literals when re-opened");
        var permissionProjects = new ProjectStore(permissionRoot);
        var permissionWorkspaceTools = new WorkspaceTools(workspace);
        var permissionBroker = new FolderAccessRequestBroker();
        var permissionEvents = new List<ChatRunEvent>();
        var permissionTools = new FolderAccessRequestTools(
            permissionSessionId, null, permissionStore, permissionSessions, permissionProjects,
            permissionWorkspaceTools, permissionBroker, permissionEvents.Add);
        var pendingPermission = permissionTools.RequestFolderAccessAsync(
            deniedAccess, "Read the explicitly requested external fixture.", CancellationToken.None);
        Assert(permissionEvents.Count == 1 && permissionEvents[0].Kind == "folder_access_request" &&
               permissionEvents[0].ActivityId is not null,
            "folder access tool emits an interactive approval card");
        Assert(permissionBroker.Submit(permissionEvents[0].ActivityId!, "session"),
            "folder access approval submission resumes the pending tool");
        var permissionResult = await pendingPermission;
        Assert(permissionResult.Contains("granted session") &&
               permissionSessions.Get(permissionSessionId)!.AllowedDirectories.Contains(deniedAccess) &&
               permissionWorkspaceTools.ReadFile(Path.Combine(deniedAccess, "blocked.txt")).Contains("approval granted"),
            "approved folder persists at session scope and becomes available in the same Agent run");

        var networkBroker = new NetworkAccessRequestBroker();
        var networkEvents = new List<ChatRunEvent>();
        var networkSecurityEndpoints = new List<string>();
        var networkSecurity = new SecurityPolicyContext(
            permissionSessionId, permissionRoot, networkSecurityEndpoints, permissionStore.SecurityAudit);
        var networkTools = new NetworkAccessRequestTools(
            permissionSessionId, null, permissionStore, permissionSessions, permissionProjects,
            networkSecurity, networkBroker, networkEvents.Add);
        var pendingNetwork = networkTools.RequestNetworkAccessAsync(
            "packages.example.com", 443, "Download the package requested by the user.", CancellationToken.None);
        Assert(networkEvents.Count == 1 && networkEvents[0].Kind == "network_access_request" &&
               networkEvents[0].ActivityId is not null,
            "network access tool emits an interactive exact-endpoint approval card");
        Assert(networkBroker.Submit(networkEvents[0].ActivityId!, "session"),
            "network access approval submission resumes the pending tool");
        var networkResult = await pendingNetwork;
        Assert(networkResult.Contains("granted session") &&
               permissionSessions.Get(permissionSessionId)!.AllowedNetworkEndpoints.Contains("packages.example.com:443") &&
               NetworkAccessPolicy.IsAllowed("packages.example.com:443", networkSecurity.AllowedNetworkEndpoints),
            "approved network endpoint persists at session scope and hot-applies to the current run");

        var vaultRequestBroker = new VaultSecretRequestBroker();
        var vaultRequestEvents = new List<ChatRunEvent>();
        var vaultRequestTools = new VaultSecretRequestTools(
            permissionSessionId, permissionStore, vaultRequestBroker, vaultRequestEvents.Add);
        var pendingVaultRequest = vaultRequestTools.RequestVaultSecretAsync(
            "CHAT_TEST_TOKEN", "Authenticate the user-requested test integration.", CancellationToken.None);
        Assert(vaultRequestEvents.Count == 1 && vaultRequestEvents[0].Kind == "vault_secret_request" &&
               vaultRequestEvents[0].ActivityId is not null &&
               !vaultRequestEvents[0].Text.Contains("chat-secret-value"),
            "missing Vault key emits an interactive request without secret material");
        permissionStore.Vault.Save("CHAT_TEST_TOKEN", "chat-secret-value");
        Assert(vaultRequestBroker.Submit(vaultRequestEvents[0].ActivityId!, true, "CHAT_TEST_TOKEN"),
            "secure Vault dialog completion resumes the pending Agent tool");
        var vaultRequestResult = await pendingVaultRequest;
        Assert(vaultRequestResult.Contains("CHAT_TEST_TOKEN") &&
               !vaultRequestResult.Contains("chat-secret-value"),
            "Agent receives only the stored Vault entry name, never the secret value");

        var webHandler = new StubWebHandler();
        var webEvents = new List<ChatRunEvent>();
        var webSecurity = new SecurityPolicyContext(
            permissionSessionId, permissionRoot, new List<string>(), permissionStore.SecurityAudit);
        var webNetworkTools = new NetworkAccessRequestTools(
            permissionSessionId, null, permissionStore, permissionSessions, permissionProjects,
            webSecurity, networkBroker, webEvents.Add);
        var webTools = new WebResearchTools(webSecurity, webNetworkTools, webHandler);
        var pendingFetch = webTools.FetchUrlAsync("https://docs.example.com/latest", 4_000, CancellationToken.None);
        Assert(webEvents.Count == 1 && webEvents[0].Kind == "network_access_request",
            "native fetch automatically emits exact-endpoint approval instead of asking in prose");
        Assert(networkBroker.Submit(webEvents[0].ActivityId!, "session"),
            "native fetch approval resumes the pending HTTP request");
        var fetchedPage = await pendingFetch;
        Assert(fetchedPage.Contains("SOURCE: https://docs.example.com/latest") &&
               fetchedPage.Contains("TITLE: Official Release") &&
               fetchedPage.Contains("Current model details") && !fetchedPage.Contains("alert('bad')"),
            "native fetch returns bounded readable HTML without script content");

        var searchSecurity = new SecurityPolicyContext(
            permissionSessionId, permissionRoot, ["html.duckduckgo.com:443"], permissionStore.SecurityAudit);
        var searchTools = new WebResearchTools(searchSecurity, handler: webHandler);
        var searchResult = await searchTools.SearchWebAsync("NVIDIA Nemotron", 5, CancellationToken.None);
        Assert(searchResult.Contains("NVIDIA Nemotron Official") &&
               searchResult.Contains("https://www.nvidia.com/nemotron") &&
               searchResult.Contains("Official model page"),
            "native search parses result titles, canonical URLs, and snippets");

        var redirectSecurity = new SecurityPolicyContext(
            permissionSessionId, permissionRoot, ["redirect.example.com:443"], permissionStore.SecurityAudit);
        var redirectTools = new WebResearchTools(redirectSecurity, handler: webHandler);
        var redirectResult = await redirectTools.FetchUrlAsync(
            "https://redirect.example.com/start", 4_000, CancellationToken.None);
        Assert(redirectResult.Contains("NETWORK_DENIED") && redirectResult.Contains("cdn.example.com:443"),
            "native fetch revalidates every redirect destination against exact network policy");

        var interaction = new ProjectInteractionBroker();
        var inputEvents = new List<ChatRunEvent>();
        var pendingInput = interaction.RequestAsync("ses_probe", "무엇을 자동화할까요?", "문서|메일", "예: 월간 보고서", 20, inputEvents.Add, CancellationToken.None);
        Assert(inputEvents.Count == 1 && inputEvents[0].Kind == "input_request" && inputEvents[0].ActivityId is not null,
            "project input card event");
        var requestId = inputEvents[0].ActivityId!;
        Assert(interaction.Submit(requestId, "문서"), "project input submission");
        Assert(await pendingInput == "문서", "project input resumes agent tool");
        Assert(reopened.SaveSettings(
            server.Endpoint, "gemma4:31b", workspace, apiKey: "", maxOutputTokens: 321,
            contextWindowTokens: 131_072, maxTokensField: "max_tokens",
            temperature: 0.2, topP: 0.8, requestTimeoutSeconds: 30,
            frequencyPenalty: 0, presencePenalty: 0, seed: 0),
            "blank API key preserves credential");

        await using var harness = new LlmHarness(reopened);
        var sessionRunStates = new List<(string SessionId, bool IsResponding)>();
        harness.SessionRunStateChanged += (sessionId, isResponding) =>
            sessionRunStates.Add((sessionId, isResponding));
        Assert(await harness.GetVersionAsync() == "Microsoft Agent Framework 1.27.0", "agent framework version");
        Assert((await harness.ListSessionsAsync()).Count == 0, "new session list empty");
        var projects = await harness.ListProjectsAsync();
        Assert(projects.Count == 1 && projects[0].Name == "sample-app", "project discovery");
        var resource = await harness.AddProjectResourceAsync(projects[0].Id, "note", "업무 메모", "", "고객 승인 후 실행");
        Assert((await harness.ListProjectResourcesAsync(projects[0].Id)).Any(item => item.Id == resource.Id), "project resource persisted");
        var projectMemory = await harness.AddMemoryAsync(projects[0].Id, "constraint", "외부 서버 업로드 금지");
        Assert((await harness.ListMemoryAsync(projects[0].Id)).Any(item => item.Id == projectMemory.Id), "project memory persisted");
        var contextTools = new ProjectContextTools(projects[0].Id, reopened.Resources, reopened.Memory);
        Assert(contextTools.ReadProjectResource(resource.Id).Contains("고객 승인"), "agent reads project resource");
        Assert(contextTools.SearchMemory("외부 서버").Contains("업로드 금지"), "agent searches project memory");

        var events = new List<string>();
        const string visibleRunPrompt = "For sample-app, read the probe and reply briefly.\n\n첨부 파일:\n- Avalonia.Base 3.dll";
        var run = await harness.RunAsync(null, visibleRunPrompt,
            item => events.Add(item.Kind + ":" + item.Text), agentContext: "Internal binary handling context.");
        Assert(run.SessionId.StartsWith("ses_", StringComparison.Ordinal), "local session ID");
        Assert(run.Answer == "mock answer", "final answer");
        Assert(sessionRunStates.Count == 2 && sessionRunStates[0].IsResponding &&
               !sessionRunStates[1].IsResponding && sessionRunStates[0].SessionId == run.SessionId &&
               sessionRunStates[1].SessionId == run.SessionId,
            "session run state begins and ends around the streamed response");
        Assert(events.Any(item => item.StartsWith("reasoning:checking workspace", StringComparison.Ordinal)), "reasoning stream event");
        Assert(events.Any(item => item.StartsWith("tool_call:read_file", StringComparison.Ordinal)), "tool call event");
        Assert(events.Any(item => item.StartsWith("tool_result:read_file", StringComparison.Ordinal)), "tool result event");
        Assert(events.Any(item => item.Contains("read_file 실행 중", StringComparison.Ordinal)), "tool status event");
        Assert(events.Any(item => item.StartsWith("project_suggestion:", StringComparison.Ordinal)), "general chat project suggestion");
        Assert((await harness.ListSessionsAsync()).Any(session => session.Id == run.SessionId), "created session listed");
        await harness.SetProjectAllowedDirectoriesAsync(projects[0].Id, [projectAccess]);
        await harness.SetSessionAllowedDirectoriesAsync(run.SessionId, [sessionAccess]);
        var scopedProject = (await harness.ListProjectsAsync()).Single(item => item.Id == projects[0].Id);
        var scopedSession = await harness.GetSessionAsync(run.SessionId);
        Assert(scopedProject.AllowedDirectories.SequenceEqual([projectAccess]),
            "project folder permissions persist independently");
        Assert(scopedSession is not null && scopedSession.AllowedDirectories.SequenceEqual([sessionAccess]),
            "session folder permissions persist independently");
        var saved = await harness.LoadMessagesAsync(run.SessionId);
        Assert(saved.Count == 2 && saved[0].Role == "user" && saved[0].Text == visibleRunPrompt &&
               !saved[0].Text.Contains("Internal binary handling context") && saved[1].Text == "mock answer",
            "session messages persist only the user-visible attachment prompt");
        await harness.MoveSessionToProjectAsync(run.SessionId, projects[0].Id);
        Assert((await harness.ListGeneralSessionsAsync()).Count == 0, "moved session leaves general history");
        Assert((await harness.ListProjectSessionsAsync(projects[0].Id)).Any(session => session.Id == run.SessionId),
            "moved session appears in project history");

        var requests = server.Requests.ToArray();
        Assert(requests.Length == 2, "tool loop made two model requests");
        Assert(requests.All(request => request.Authorization == "Bearer secret-value"), "API key bearer header");
        using (var first = JsonDocument.Parse(requests[0].Body))
        {
            var body = first.RootElement;
            var tokenProperty = body.TryGetProperty("max_tokens", out var maxTokens)
                ? maxTokens : body.GetProperty("max_completion_tokens");
            Assert(tokenProperty.GetInt32() == 321, "max output tokens sent");
            Assert(body.TryGetProperty("max_tokens", out _), "legacy max_tokens compatibility policy");
            Assert(body.GetProperty("temperature").GetDouble() == 0.2, "temperature sent");
            Assert(body.GetProperty("top_p").GetDouble() == 0.8, "top-p sent");
            Assert(body.GetProperty("frequency_penalty").GetDouble() == 0, "frequency penalty sent");
            Assert(body.GetProperty("presence_penalty").GetDouble() == 0, "presence penalty sent");
            Assert(body.GetProperty("seed").GetInt32() == 0, "seed sent");
            Assert(body.GetProperty("reasoning_effort").GetString() == "high", "reasoning effort sent");
            Assert(body.GetProperty("tools").GetArrayLength() >= 6, "native tool catalog sent");
            var toolNames = body.GetProperty("tools").EnumerateArray()
                .Select(tool => tool.GetProperty("function").GetProperty("name").GetString()).ToHashSet();
            Assert(toolNames.Contains("start_background_task") && toolNames.Contains("create_cron_job"),
                "background and cron tools sent to Agent Framework");
            Assert(toolNames.Contains("create_reminder") && toolNames.Contains("glob_files") &&
                   toolNames.Contains("apply_edits") && toolNames.Contains("git_status") &&
                   toolNames.Contains("list_allowed_directories"),
                "local reminder, discovery, editing, Git, and access-boundary tools sent to Agent Framework");
            Assert(toolNames.Contains("create_or_update_skill") && toolNames.Contains("validate_skill") &&
                   toolNames.Contains("list_local_skills"),
                "project-local skill creator tools sent to Agent Framework");
            Assert(toolNames.Contains("request_folder_access"),
                "interactive folder access request tool sent to Agent Framework");
            Assert(toolNames.Contains("request_network_access") && toolNames.Contains("security_status") &&
                   toolNames.Contains("list_security_audit"),
                "OpenShell-inspired network approval and audit tools sent to Agent Framework");
            Assert(toolNames.Contains("request_vault_secret"),
                "secure in-chat Vault input tool sent to Agent Framework");
            Assert(toolNames.Contains("ask_user_question") && toolNames.Contains("ask_calendar_question") &&
                   toolNames.Contains("finalize_calendar_event"),
                "ordinary chat exposes native decision cards and confirmed calendar registration tools");
            Assert(toolNames.Contains("web_search") && toolNames.Contains("fetch_url"),
                "native web research tools sent to Agent Framework");
            Assert(toolNames.Contains("delegate_task") && toolNames.Contains("review_result"),
                "bounded worker delegation and read-only review tools sent to Agent Framework");
            var system = body.GetProperty("messages")[0].GetProperty("content").GetString() ?? "";
            Assert(system.StartsWith("<|think|>", StringComparison.Ordinal), "Gemma 4 thinking token injected");
            Assert(system.Contains("native coding harness", StringComparison.OrdinalIgnoreCase), "bundled system prompt loaded");
            Assert(system.Contains("Systematic debugging", StringComparison.OrdinalIgnoreCase), "bundled skills loaded");
            Assert(system.Contains("Skill creator", StringComparison.OrdinalIgnoreCase) && system.Contains("Release checks"),
                "bundled skill creator and newly created local skill loaded");
            Assert(system.Contains("Multi-Agent coordination", StringComparison.Ordinal),
                "lead Agent receives selective delegation and review guidance");
            Assert(system.Contains("Native user interaction", StringComparison.Ordinal) &&
                   system.Contains("ask_user_question", StringComparison.Ordinal) &&
                   system.Contains("finalize_calendar_event", StringComparison.Ordinal),
                "ordinary chat is instructed to use native cards for decisions and calendar saving");
        }
        using (var second = JsonDocument.Parse(requests[1].Body))
        {
            var messages = second.RootElement.GetProperty("messages");
            Assert(messages.EnumerateArray().Any(message => message.GetProperty("role").GetString() == "tool" &&
                (message.GetProperty("content").GetString() ?? "").Contains("native tool result")), "native tool result returned to model");
        }

        var flowRoot = Path.Combine(root, "transient-flows");
        Directory.CreateDirectory(flowRoot);
        await using (var flowServer = new MockOpenAiServer(directAnswer: "설문 응답"))
        {
            var flowStore = new LocalStore(flowRoot);
            flowStore.SaveSettings(flowServer.Endpoint, "model", workspace);
            await using var flowHarness = new LlmHarness(flowStore);
            var flowStates = new List<(string, bool)>();
            flowHarness.SessionRunStateChanged += (id, running) => flowStates.Add((id, running));
            var projectFlow = await flowHarness.RunAsync(null, "프로젝트 설문 시작", projectSetup: true);
            var automationFlow = await flowHarness.RunAsync(null, "작업 설문 시작", automationSetup: "task");
            Assert(projectFlow.SessionId.StartsWith("flow_", StringComparison.Ordinal) &&
                   automationFlow.SessionId.StartsWith("flow_", StringComparison.Ordinal) &&
                   (await flowHarness.ListSessionsAsync()).Count == 0 && flowStates.Count == 0 &&
                   !File.Exists(Path.Combine(flowRoot, "harness-sessions.json")),
                "project and automation surveys are transient workflows, not hidden or visible chat sessions");
            using var projectSurveyRequest = JsonDocument.Parse(flowServer.Requests.First().Body);
            var projectSurveyInstructions = projectSurveyRequest.RootElement.GetProperty("messages")[0]
                .GetProperty("content").GetString() ?? "";
            Assert(projectSurveyInstructions.Contains("web-app-delivery", StringComparison.Ordinal) &&
                   projectSurveyInstructions.Contains("Next.js server exclusively", StringComparison.Ordinal) &&
                   projectSurveyInstructions.Contains("External access uses ngrok", StringComparison.Ordinal),
                "project survey reads the editable delivery stack from the skill instead of C# constants");
        }

        var parallelRoot = Path.Combine(root, "parallel-session-runs");
        Directory.CreateDirectory(parallelRoot);
        await using (var parallelServer = new MockOpenAiServer(
                         directAnswer: "parallel answer", responseDelayMilliseconds: 350))
        {
            var parallelStore = new LocalStore(parallelRoot);
            parallelStore.SaveSettings(parallelServer.Endpoint, "model", workspace);
            await using var parallelHarness = new LlmHarness(parallelStore);
            var firstParallel = parallelHarness.RunAsync(
                null, "first session", serializeRun: false, sessionTitle: "first");
            var secondParallel = parallelHarness.RunAsync(
                null, "second session", serializeRun: false, sessionTitle: "second");
            IReadOnlyList<ChatSession> responding = [];
            for (var attempt = 0; attempt < 20 && responding.Count < 2; attempt++)
            {
                await Task.Delay(25);
                responding = await parallelHarness.ListRespondingSessionsAsync();
            }
            Assert(responding.Count == 2,
                "two different chat sessions are reported as responding at the same time");
            await AssertThrowsAsync<InvalidOperationException>(
                () => parallelHarness.DeleteSessionAsync(responding[0].Id),
                "a responding conversation cannot be deleted while its stream is active");
            var parallelResults = await Task.WhenAll(firstParallel, secondParallel);
            Assert(parallelServer.MaxConcurrentRequests >= 2 &&
                   parallelResults.Select(result => result.SessionId).Distinct().Count() == 2 &&
                   (await parallelHarness.ListRespondingSessionsAsync()).Count == 0,
                "session runs bypass the global gate and execute as independent concurrent LLM requests");
            await parallelHarness.DeleteSessionAsync(parallelResults[0].SessionId);
            Assert((await parallelHarness.GetSessionAsync(parallelResults[0].SessionId)) is null,
                "a completed conversation can be deleted through the harness API");
        }

        Assert(File.Exists(Path.Combine(root, "harness-sessions.json")), "local session file created");
        Assert(!reopened.SaveSettings(
            server.Endpoint, "model", workspace, removeApiKey: true, maxOutputTokens: 321,
            contextWindowTokens: 131_072, maxTokensField: "max_tokens",
            temperature: 0.2, topP: 0.8, requestTimeoutSeconds: 30,
            frequencyPenalty: 0, presencePenalty: 0, seed: 0), "API key removal status");
        Assert(reopened.GetApiKey() is null, "API key removed");
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

Console.WriteLine("silaa Microsoft Agent Framework checks passed");

sealed class StubWebHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI is required.");
        if (uri.Host == "html.duckduckgo.com")
        {
            const string html = """
                <html><body>
                <div class="result"><a class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fwww.nvidia.com%2Fnemotron">NVIDIA <b>Nemotron</b> Official</a>
                <a class="result__snippet">Official model page</a></div>
                </body></html>
                """;
            return Task.FromResult(Html(HttpStatusCode.OK, html));
        }
        if (uri.Host == "docs.example.com")
            return Task.FromResult(Html(HttpStatusCode.OK,
                "<html><head><title>Official Release</title><script>alert('bad')</script></head><body><h1>Current model details</h1></body></html>"));
        if (uri.Host == "redirect.example.com")
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://cdn.example.com/final");
            return Task.FromResult(response);
        }
        if (uri.Host == "cdn.example.com")
            return Task.FromResult(Html(HttpStatusCode.OK, "<html><body>redirected</body></html>"));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static HttpResponseMessage Html(HttpStatusCode status, string content) => new(status)
    {
        Content = new StringContent(content, Encoding.UTF8, "text/html")
    };
}

sealed class MockOpenAiServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly bool _streamPartialUntilCancelled;
    private readonly string? _directAnswer;
    private readonly int _responseDelayMilliseconds;
    private int _activeRequests;
    private int _maxConcurrentRequests;

    public MockOpenAiServer(bool streamPartialUntilCancelled = false, string? directAnswer = null,
        int responseDelayMilliseconds = 0)
    {
        _streamPartialUntilCancelled = streamPartialUntilCancelled;
        _directAnswer = directAnswer;
        _responseDelayMilliseconds = responseDelayMilliseconds;
        _listener.Start();
        Endpoint = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1";
        _loop = AcceptAsync();
    }

    public string Endpoint { get; }
    public ConcurrentQueue<CapturedRequest> Requests { get; } = new();
    public int MaxConcurrentRequests => Volatile.Read(ref _maxConcurrentRequests);

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = HandleAsync(client);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
            var requestLine = await reader.ReadLineAsync();
            if (requestLine is null) return;
            var contentLength = 0;
            string? authorization = null;
            string? header;
            while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync()))
            {
                if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(header["Content-Length:".Length..].Trim(), out contentLength);
                else if (header.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                    authorization = header["Authorization:".Length..].Trim();
            }
            var buffer = new char[contentLength];
            var read = 0;
            while (read < buffer.Length)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(read, buffer.Length - read));
                if (count == 0) break;
                read += count;
            }
            var requestBody = new string(buffer, 0, read);
            Requests.Enqueue(new CapturedRequest(requestLine, authorization, requestBody));
            var active = Interlocked.Increment(ref _activeRequests);
            int observed;
            while (active > (observed = Volatile.Read(ref _maxConcurrentRequests)) &&
                   Interlocked.CompareExchange(ref _maxConcurrentRequests, active, observed) != observed) { }
            try
            {
                if (_responseDelayMilliseconds > 0)
                    await Task.Delay(_responseDelayMilliseconds, _stop.Token);

                if (_streamPartialUntilCancelled)
                {
                    var headers = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(headers);
                    var partial = Encoding.UTF8.GetBytes(
                        "data: {\"id\":\"partial\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"partial answer\"},\"finish_reason\":null}]}\n\n");
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"{partial.Length:X}\r\n"));
                    await stream.WriteAsync(partial);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("\r\n"));
                    await stream.FlushAsync();
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, _stop.Token); }
                    catch (OperationCanceledException) { }
                    return;
                }

                using var requestJson = JsonDocument.Parse(requestBody);
                var hasToolResult = requestJson.RootElement.GetProperty("messages").EnumerateArray()
                    .Any(message => message.GetProperty("role").GetString() == "tool");
                var body = _directAnswer is not null
                    ? DirectAnswerBody(_directAnswer)
                    : hasToolResult
                        ? "data: {\"id\":\"mock\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"mock answer\"},\"finish_reason\":null}]}\n\ndata: {\"id\":\"mock\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"
                        : "data: {\"id\":\"mock\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"reasoning\":\"checking workspace\"},\"finish_reason\":null}]}\n\ndata: {\"id\":\"mock\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"path\\\":\\\"probe.txt\\\"}\"}}]},\"finish_reason\":null}]}\n\ndata: {\"id\":\"mock\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\ndata: [DONE]\n\n";
                var payload = Encoding.UTF8.GetBytes(body);
                var response = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response);
                await stream.WriteAsync(payload);
            }
            finally
            {
                Interlocked.Decrement(ref _activeRequests);
            }
        }
    }

    public sealed record CapturedRequest(string RequestLine, string? Authorization, string Body);

    private static string DirectAnswerBody(string answer) =>
        $"data: {{\"id\":\"direct\",\"object\":\"chat.completion.chunk\",\"choices\":[{{\"index\":0,\"delta\":{{\"role\":\"assistant\",\"content\":{JsonSerializer.Serialize(answer)}}},\"finish_reason\":null}}]}}\n\n" +
        "data: {\"id\":\"direct\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try { await _loop; } catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}

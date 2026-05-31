# Phase 2: Key rotation + `.env` hardening - Pattern Map

**Mapped:** 2026-05-30
**Files analyzed:** 14 (5 new, 5 edit, 3 delete, 1 doc)
**Analogs found:** 12 / 14 (2 doc-only files have no code analog)

> Pattern mapping was performed against CONTEXT.md, RESEARCH.md and UI-SPEC.md. The phase is 90% wiring — every C# pattern below already exists in the codebase and is being re-used verbatim. Only one new XAML window is introduced (FirstRunWindow), and it copies its theme from `SettingsWindow.xaml` and `CommandConfirmationWindow.xaml` verbatim.

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `AIBWindows/Views/FirstRunWindow.xaml` (NEW) | view (modal window XAML) | request-response (dialog) | `AIBWindows/Views/SettingsWindow.xaml` + `AIBWindows/Views/CommandConfirmationWindow.xaml` | exact (theme + dialog shape) |
| `AIBWindows/Views/FirstRunWindow.xaml.cs` (NEW) | view code-behind | request-response (dialog → vault write) | `AIBWindows/Views/SettingsWindow.xaml.cs` + `AIBWindows/Views/CommandConfirmationWindow.xaml.cs` | exact (Save/Cancel + DialogResult pattern) |
| `.env.example` (NEW, repo root) | config doc | static template | none in repo | no analog (doc-only — see `.env` for placeholder shape) |
| `.planning/phases/02-key-rotation-env-hardening/evidence/.gitkeep` (NEW) | filesystem marker | n/a | none | no analog (zero-byte placeholder) |
| `AIBWindows/Services/OpenAIService.cs` (EDIT lines 651-690, 696-725) | service | request-response (vault lookup before HTTP call) | self (existing Ollama-branch sentinel at lines 659-667) | exact (same control-flow shape) |
| `AIBWindows/App.xaml.cs` (EDIT OnStartup + OnHotkeyDetected) | application entry / hotkey handler | event-driven | self (existing OnStartup block at lines 26-81) | exact (in-place mutation) |
| `AIBWindows/AIB.csproj` (EDIT line 17 — delete DotNetEnv) | build config | n/a | n/a | n/a (one-line deletion) |
| `.env` (DELETE, repo root) | filesystem | n/a | n/a | n/a |
| `AIBWindows/.env` (DELETE) | filesystem | n/a | n/a | n/a |
| `AIBLinux/.env` (DELETE) | filesystem | n/a | n/a | n/a |
| `Regras de Identidade/SEGURANCA.MD` (EDIT §2) | doc | n/a | self (existing prose at line 11-14) | exact (extend existing §2) |
| `README.md` (EDIT key-setup section) | doc | n/a | self (existing line 30 "Arquivo `.env`…") | exact (replace one bullet) |
| `AIBWindows/Services/CredentialService.cs` | READ-ONLY (call site) | — | — | — |
| `AIBWindows/Services/SettingsService.cs` | READ-ONLY (call site) | — | — | — |
| `AIBWindows/Services/AuditLogService.cs` | READ-ONLY (call site) | — | — | — |

---

## Pattern Assignments

### `AIBWindows/Views/FirstRunWindow.xaml` (NEW view, dialog)

**Analog:** `AIBWindows/Views/SettingsWindow.xaml` (primary — theme + resources + Save/Cancel layout)
**Secondary analog:** `AIBWindows/Views/CommandConfirmationWindow.xaml` (centered dialog with `WindowStartupLocation=CenterOwner`-style behaviour)

**Window declaration pattern** (copy from `SettingsWindow.xaml:1-11`):
```xml
<Window x:Class="AIB.Views.SettingsWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Configurações AIB"
        Height="650" Width="450"
        WindowStyle="None"
        AllowsTransparency="True"
        Background="#01000000"
        WindowStartupLocation="CenterOwner"
        ShowInTaskbar="False"
        MouseDown="Window_MouseDown">
```

For FirstRunWindow, override per UI-SPEC: `Title="Bem-vindo ao AIB"`, `Width="480"`, `SizeToContent="Height"` + `MinHeight=420` + `MaxHeight=600`, `WindowStartupLocation="CenterScreen"` (D-07), `Topmost="True"`, `ResizeMode="NoResize"`.

**Resource dictionary pattern — REUSE VERBATIM** (copy from `SettingsWindow.xaml:13-63`):
```xml
<Window.Resources>
    <SolidColorBrush x:Key="FlyoutBg" Color="#EE121214" />
    <SolidColorBrush x:Key="SystemBorder" Color="#3FFFFFFF" />
    <SolidColorBrush x:Key="SystemText" Color="#F2F2F5" />
    <SolidColorBrush x:Key="InputBg" Color="#15FFFFFF" />

    <LinearGradientBrush x:Key="AiAccent" StartPoint="0,0" EndPoint="1,1">
        <GradientStop Color="#2B5BFF" Offset="0"/>
        <GradientStop Color="#6A3BFF" Offset="1"/>
    </LinearGradientBrush>

    <Style TargetType="TextBlock">
        <Setter Property="Foreground" Value="{StaticResource SystemText}"/>
        <Setter Property="FontSize" Value="13"/>
        <Setter Property="Margin" Value="0,12,0,4"/>
    </Style>

    <Style TargetType="TextBox">
        <Setter Property="Background" Value="{StaticResource InputBg}"/>
        <Setter Property="Foreground" Value="White"/>
        <Setter Property="BorderBrush" Value="#2A2A30"/>
        <Setter Property="Padding" Value="8"/>
        <Setter Property="FontSize" Value="13"/>
    </Style>

    <Style TargetType="ComboBox">
        <Setter Property="Background" Value="{StaticResource InputBg}"/>
        <Setter Property="Padding" Value="5"/>
    </Style>
</Window.Resources>
```

**Gradient frame + DropShadow + inner dark surface** (copy from `SettingsWindow.xaml:65-84`):
```xml
<Window.Effect>
    <DropShadowEffect BlurRadius="28" ShadowDepth="6" Direction="270"
                      Color="#000000" Opacity="0.55"/>
</Window.Effect>

<Border CornerRadius="16" Padding="2">
    <Border.Background>
        <LinearGradientBrush StartPoint="0,0" EndPoint="1,1">
            <GradientStop Color="#9B51E0" Offset="0"/>
            <GradientStop Color="#3182CE" Offset="0.33"/>
            <GradientStop Color="#D53F8C" Offset="0.66"/>
            <GradientStop Color="#E28743" Offset="1"/>
        </LinearGradientBrush>
    </Border.Background>

    <Border CornerRadius="14" Background="#F01A1A1E">
        <Grid Margin="25">
            <!-- 3-row layout: title / content / footer-buttons -->
```

**Title block + accent underline pattern** (copy from `SettingsWindow.xaml:91-95`):
```xml
<StackPanel Grid.Row="0">
    <TextBlock Text="CONFIGURAÇÕES" FontSize="18" FontWeight="Bold" Margin="0,0,0,5" Foreground="White"/>
    <TextBlock Text="Personalize a conexão e o comportamento do AIB." Margin="0,0,0,10" Opacity="0.6"/>
    <Border Height="1" Background="{StaticResource AiAccent}" Opacity="0.5" Margin="0,0,0,10"/>
</StackPanel>
```

For FirstRunWindow: replace strings with UI-SPEC copy ("Bem-vindo ao AIB" / "Para começar, escolha seu provedor de IA.") and bump `FontSize` to `22` for the display title.

**Footer-buttons pattern (Cancel + Salvar)** (copy verbatim from `SettingsWindow.xaml:194-205`):
```xml
<StackPanel Grid.Row="2" Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,20,0,0">
    <Button Content="Cancelar" Click="Cancel_Click" Width="80" Height="35" Background="Transparent" Foreground="White" BorderThickness="0" Cursor="Hand" Margin="0,0,10,0"/>
    <Button Content="Salvar" Click="Save_Click" Width="100" Height="35" Cursor="Hand">
        <Button.Template>
            <ControlTemplate TargetType="Button">
                <Border Background="{StaticResource AiAccent}" CornerRadius="6">
                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                </Border>
            </ControlTemplate>
        </Button.Template>
    </Button>
</StackPanel>
```

For FirstRunWindow Cancel button add `IsCancel="True"` (gives Esc → DialogResult=false free) and Salvar add `IsDefault="True"` (Enter triggers Save).

**Ollama model picker pattern** (copy from `SettingsWindow.xaml:114-118`):
```xml
<TextBlock Text="Modelo LLM (Principal)"/>
<Grid>
    <ComboBox x:Name="ModelComboBox" IsEditable="True" Height="38" VerticalContentAlignment="Center"/>
    <ProgressBar x:Name="LoadingProgress" IsIndeterminate="True" Height="2" VerticalAlignment="Bottom" Visibility="Collapsed"/>
</Grid>
```

---

### `AIBWindows/Views/FirstRunWindow.xaml.cs` (NEW view code-behind)

**Analog:** `AIBWindows/Views/SettingsWindow.xaml.cs` (primary — settings load/save + Ollama models refresh) + `AIBWindows/Views/CommandConfirmationWindow.xaml.cs` (DialogResult pattern)

**Imports + constructor pattern** (copy from `SettingsWindow.xaml.cs:1-22`):
```csharp
using System;
using System.Linq;
using System.Windows;
using AIB.Services;

namespace AIB.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settingsService = new();
    private UserAppSettings _currentSettings;

    public SettingsWindow()
    {
        InitializeComponent();
        _currentSettings = _settingsService.LoadSettings();
        LoadUiValues();
        // Não bloqueia o UI Thread
        Dispatcher.BeginInvoke(new Action(async () => await RefreshModelsAsync()));
    }
```

For FirstRunWindow: same shape; constructor calls `RefreshModelsAsync()` on Ollama branch (default) via `Dispatcher.BeginInvoke`.

**DialogResult Save/Cancel pattern** (copy from `CommandConfirmationWindow.xaml.cs:21-34`):
```csharp
private void Allow_Click(object sender, RoutedEventArgs e)
{
    IsAllowed = true;
    AlwaysAllow = AlwaysAllowCheckBox.IsChecked ?? false;
    DialogResult = true;
    Close();
}

private void Deny_Click(object sender, RoutedEventArgs e)
{
    IsAllowed = false;
    DialogResult = false;
    Close();
}
```

For FirstRunWindow:
- `Save_Click` → `DialogResult = true; Close();` (after vault write + audit succeeds)
- `Cancel_Click` → `DialogResult = false; Close();` (parent App.xaml.cs handles `Application.Current.Shutdown()` based on the false result — D-04 + Pitfall 6)

**Ollama models refresh pattern** (copy from `SettingsWindow.xaml.cs:91-113`):
```csharp
private async System.Threading.Tasks.Task RefreshModelsAsync()
{
    LoadingProgress.Visibility = Visibility.Visible;
    try
    {
        var models = await _settingsService.GetOllamaModelsAsync(UrlTextBox.Text);
        if (models.Any())
        {
            var currentModel = ModelComboBox.Text;
            var currentShadowModel = ShadowModelComboBox.Text;

            ModelComboBox.ItemsSource = models;
            ShadowModelComboBox.ItemsSource = models;

            ModelComboBox.Text = currentModel;
            ShadowModelComboBox.Text = currentShadowModel;
        }
    }
    finally
    {
        LoadingProgress.Visibility = Visibility.Collapsed;
    }
}
```

For FirstRunWindow: simpler — single `ModelComboBox`, no shadow combo; if `models.Count == 0` show the OllamaErrorBlock (UI-SPEC state S3) instead of silently leaving combo empty.

**Settings load/mutate/save pattern** (copy from `SettingsWindow.xaml.cs:120-141`):
```csharp
private void Save_Click(object sender, RoutedEventArgs e)
{
    _currentSettings.AiProvider = (ProviderComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? ProviderComboBox.Text;
    _currentSettings.ApiUrl = UrlTextBox.Text;
    _currentSettings.ApiKey = KeyTextBox.Text;
    _currentSettings.ModelName = ModelComboBox.Text;
    _currentSettings.ShadowModelName = ShadowModelComboBox.Text;
    // …
    _settingsService.SaveSettings(_currentSettings);
    DirectoryService.ApplyFromSettings(_currentSettings);
    Close();
}
```

For FirstRunWindow Save_Click — OpenAI branch:
```csharp
// 1. Regex validate; on fail set ErrorLabel + audit firstrun_invalid_key; return.
if (!System.Text.RegularExpressions.Regex.IsMatch(key, @"^sk-[a-zA-Z0-9_-]{20,}$")) { /* … */ return; }

// 2. Vault write (see Shared Pattern: Vault Write)
string result = await CredentialService.StoreCredentialAsync("openai", "ApiKey", key);
if (result.StartsWith("ERRO")) { /* show inline error; return */ }

// 3. Settings mutation (sentinel pattern — see Shared Pattern: Sentinel)
var settings = _settingsService.LoadSettings();
settings.AiProvider = "OpenAI";      // RESEARCH Q1 resolution
settings.ApiKey = "use-vault";       // D-06 sentinel
settings.ApiUrl = "";                // RESEARCH Q3 resolution — empty → SDK default
_settingsService.SaveSettings(settings);

// 4. Audit (see Shared Pattern: Audit Log)
_ = AuditLogService.AppendAsync(new {
    ts = DateTime.UtcNow.ToString("o"),
    outcome = "firstrun_saved",
    provider = "OpenAI",
    key_last4 = key.Length >= 4 ? key[^4..] : "----"
});

// 5. Close
DialogResult = true;
Close();
```

For FirstRunWindow Save_Click — Ollama branch:
```csharp
var settings = _settingsService.LoadSettings();
settings.AiProvider = "Ollama";
settings.ApiKey = "ollama";
settings.ModelName = selectedModel;
settings.ShadowModelName = selectedModel;   // D-09 mirror by default
_settingsService.SaveSettings(settings);
_ = AuditLogService.AppendAsync(new {
    ts = DateTime.UtcNow.ToString("o"),
    outcome = "firstrun_saved",
    provider = "Ollama",
    model = selectedModel
});
DialogResult = true;
Close();
```

---

### `AIBWindows/Services/OpenAIService.cs` (EDIT lines 651-690 and 696-725)

**Analog:** self (the existing Ollama-branch sentinel at lines 659-667 already uses the exact same control-flow shape we need to add for the vault sentinel).

**Existing Ollama sentinel** (template for the new vault-sentinel block) — current `OpenAIService.cs:659-667`:
```csharp
if (settings.AiProvider == "Ollama")
{
    if (string.IsNullOrEmpty(apiUrl)) apiUrl = "http://127.0.0.1:11434/v1";
    if (string.IsNullOrEmpty(apiKey)) apiKey = "ollama";

    // Bypass IPv6 DNS resolution issues that cause 2-minute timeouts
    apiUrl = apiUrl.Replace("localhost", "127.0.0.1");
}
if (string.IsNullOrEmpty(apiKey)) apiKey = "placeholder";
```

**New vault-sentinel block — insert BEFORE the `if (string.IsNullOrEmpty(apiKey))` placeholder fallback at both line 667 (AskStatelessAsync) and line 716 (EnsureClient):**
```csharp
// D-06: honor "use-vault" sentinel — fetch real key from DPAPI vault
if (apiKey == "use-vault")
{
    apiKey = CredentialService.RetrieveCredential("openai", "ApiKey");
    if (apiKey.StartsWith("ERRO"))
    {
        // Vault missing — should never reach here if D-03 detector ran.
        // Treat as deny: fall through to "placeholder" so OpenAI SDK fails
        // with auth error; user sees error in chat → Settings → re-runs FirstRunWindow.
        apiKey = "placeholder";
    }
}
```

**Exact insertion point — `AskStatelessAsync` (after line 666, before line 667):**
```csharp
        if (settings.AiProvider == "Ollama")
        {
            if (string.IsNullOrEmpty(apiUrl)) apiUrl = "http://127.0.0.1:11434/v1";
            if (string.IsNullOrEmpty(apiKey)) apiKey = "ollama";
            apiUrl = apiUrl.Replace("localhost", "127.0.0.1");
        }
        // ─── INSERT HERE ───
        if (apiKey == "use-vault") { /* vault lookup block above */ }
        // ─── /INSERT ───
        if (string.IsNullOrEmpty(apiKey)) apiKey = "placeholder";
```

**Exact insertion point — `EnsureClient` (after line 714, before line 716):**
```csharp
            if (settings.AiProvider == "Ollama")
            {
                if (string.IsNullOrEmpty(apiUrl)) apiUrl = "http://127.0.0.1:11434/v1";
                if (string.IsNullOrEmpty(apiKey)) apiKey = "ollama";
                apiUrl = apiUrl.Replace("localhost", "127.0.0.1");
            }
            // ─── INSERT HERE ───
            if (apiKey == "use-vault") { /* vault lookup block above */ }
            // ─── /INSERT ───
            if (string.IsNullOrEmpty(apiKey)) apiKey = "placeholder";
```

**No new imports needed.** `CredentialService` is in `AIB.Services` namespace (same as `OpenAIService`).

---

### `AIBWindows/App.xaml.cs` (EDIT OnStartup + OnHotkeyDetected)

**Analog:** self (existing OnStartup block at lines 26-81; existing OnHotkeyDetected at 83-87).

**D-11 migration — exact insertion in `OnStartup` between current lines 33 and 36:**

Existing `App.xaml.cs:30-36`:
```csharp
try
{
    DirectoryService.EnsureDirectories();
    var settings = new SettingsService().LoadSettings();
    DirectoryService.ApplyFromSettings(settings);

    _chatWindow = new ChatWindow();
```

Replace with:
```csharp
try
{
    DirectoryService.EnsureDirectories();
    var settingsService = new SettingsService();
    var settings = settingsService.LoadSettings();
    DirectoryService.ApplyFromSettings(settings);

    // D-11: one-shot migration — force re-entry by clearing any non-sentinel,
    // non-Ollama key. Idempotent: after the first run, ApiKey == "use-vault"
    // and this block is a no-op (skip on string compare).
    if (settings.ApiKey != "use-vault" && settings.ApiKey != "ollama")
    {
        bool hadKey = !string.IsNullOrEmpty(settings.ApiKey);
        settings.ApiKey = "use-vault";
        settingsService.SaveSettings(settings);
        _ = AuditLogService.AppendAsync(new
        {
            ts = DateTime.UtcNow.ToString("o"),
            outcome = "migration_clear_apikey",
            previous_key_present = hadKey
        });
        Console.WriteLine("[MIGRATION] settings.ApiKey replaced with 'use-vault' sentinel (D-11).");
    }

    _chatWindow = new ChatWindow();
```

**D-01 + D-08 hotkey branch — replace existing `OnHotkeyDetected` (current lines 83-87):**

Existing:
```csharp
private void OnHotkeyDetected(object? sender, HotkeyEventArgs e)
{
    _chatWindow?.ToggleWindow();
    e.Handled = true;
}
```

Replace with:
```csharp
private readonly SettingsService _settingsService = new();

private void OnHotkeyDetected(object? sender, HotkeyEventArgs e)
{
    e.Handled = true;
    var settings = _settingsService.LoadSettings();
    if (NeedsFirstRun(settings))
    {
        ShowFirstRunWindow();
        return;  // D-08: ChatWindow does NOT show until FirstRunWindow.Save succeeds
    }
    _chatWindow?.ToggleWindow();
}

private static bool NeedsFirstRun(UserAppSettings s)
{
    if (s.AiProvider == "Ollama") return false;
    string r = CredentialService.RetrieveCredential("openai", "ApiKey");
    return r.StartsWith("ERRO");
}

private void ShowFirstRunWindow()
{
    var win = new FirstRunWindow();
    bool? ok = win.ShowDialog();
    if (ok == true)
    {
        _chatWindow?.ToggleWindow();   // D-08 sequencing: show chat only after Save
    }
    else
    {
        _ = AuditLogService.AppendAsync(new
        {
            ts = DateTime.UtcNow.ToString("o"),
            outcome = "firstrun_cancelled"
        });
        Current.Shutdown();   // D-04: cancel = clean shutdown
    }
}
```

---

### `AIBWindows/AIB.csproj` (EDIT — delete line 17)

**No analog.** Single-line deletion. Current line 17:
```xml
<PackageReference Include="DotNetEnv" Version="3.1.1" />
```

Delete the line. No replacement.

---

### `.env` / `AIBWindows/.env` / `AIBLinux/.env` (DELETE)

All three contain the live `sk-svcacct-…` key (verified by Read of `.env`). Delete on disk (Windows PowerShell `Remove-Item` or `del`). `.gitignore:11` already ignores `.env` so no git operation is needed. Note: RESEARCH.md surfaced this is THREE files, not the two named in CONCERNS.md.

---

### `.env.example` (NEW, repo root)

**No analog.** Doc-only file with placeholders. Content per CONTEXT.md D-10:
```
# Optional — only used if you bypass FirstRunWindow and set
# environment variables manually before launching AIB.
# AIB Windows does NOT read this file; key lives in DPAPI vault
# (~/.AIB/credentials/openai.bin).
OPENAI_API_KEY=sk-PLACEHOLDER
URL=http://localhost:11434/v1
MODEL=qwen2.5:7b
```

`.gitignore:11` matches only the literal `.env` filename (no `.env*` glob — verified RESEARCH Pitfall 5), so `.env.example` IS tracked when staged.

---

### `.planning/phases/02-key-rotation-env-hardening/evidence/.gitkeep` (NEW)

**No analog.** Zero-byte file. Standard git pattern: directories that exist only to receive future artifacts (the D-12 screenshot) need a `.gitkeep` so the directory tracks before any real file is committed.

---

### `Regras de Identidade/SEGURANCA.MD` (EDIT §2)

**Analog:** self (existing §2 at line 11-14). The existing prose already mentions DPAPI; this edit makes it the EXPLICIT canonical store for the OpenAI key (currently §2 generically says "Segredos corporativos devem usar as rotinas específicas de Vault").

Existing line 14:
```
- **A API DPAPI (CredentialService):** Segredos corporativos devem usar as rotinas específicas de Vault (ex: `manage_vault` Tool). Essa classe amarra-se ao *Data Protection API* do Windows nativamente. …
```

Extend §2 with explicit statement that the OpenAI key lives in DPAPI vault (no key in `.env`, no key in `profile.dat`). Tone: present-tense shipped behaviour (NFR-03). Suggested addition (planner can polish):
```
- **A chave da OpenAI:** A chave de API da OpenAI é armazenada exclusivamente no cofre DPAPI (~/.AIB/credentials/openai.bin), nunca em `.env` ou em texto plano no `profile.dat`. A entrada inicial acontece via `FirstRunWindow` no primeiro atalho global após a instalação.
```

---

### `README.md` (EDIT key-setup section)

**Analog:** self (existing line 30 bullet). Currently:
```
- Arquivo `.env` configurado na raiz com as chaves de API necessárias (caso não use Ollama).
```

Replace with a bullet describing the FirstRunWindow walkthrough; reference the DPAPI vault (consistent with the updated SEGURANCA.MD). Planner discretion on exact prose; keep the format consistent with the surrounding `## 🛠️ Requisitos Globais` bullets.

---

## Shared Patterns

### Pattern: Vault Read (D-03 detector + D-06 OpenAIService resolver)

**Source:** `AIBWindows/Services/CredentialService.cs:53-98`
**Apply to:** `App.xaml.cs::NeedsFirstRun` (detector) AND both call sites in `OpenAIService.cs` (`AskStatelessAsync` :667, `EnsureClient` :716)

**Contract** (verified by Read of `CredentialService.cs`):
- Returns the value on hit.
- Returns one of four `ERRO`-prefixed strings on miss (lines 79, 83, 92, 96).
- Callers MUST branch on `result.StartsWith("ERRO")`, never on `result == null` (it's never null) or on exception (the call never throws — exceptions are swallowed at line 94-96).

**Idiomatic call shape:**
```csharp
string r = CredentialService.RetrieveCredential("openai", "ApiKey");
if (r.StartsWith("ERRO"))
{
    // miss path
}
else
{
    // r is the value
}
```

**Pitfall (RESEARCH §Pitfall 1):** the function has a global-fallback loop (lines 65-77) that returns the FIRST `.bin` file's value for `key="ApiKey"` if `openai.bin` is missing. False-negative window is narrow (requires another system to have stored a credential keyed `"ApiKey"`); flagged for Phase 4 fix. Do NOT try to fix in Phase 2.

### Pattern: Vault Write (D-06 Save target)

**Source:** `AIBWindows/Services/CredentialService.cs:21-51`
**Apply to:** `FirstRunWindow.xaml.cs::Save_Click` (OpenAI branch only)

**Contract:**
- Returns a success message on hit, or `ERRO ao armazenar credencial: {ex.Message}` on failure (line 49).
- Caller MUST `await` (it's `async Task<string>`).
- Caller MUST branch on `result.StartsWith("ERRO")` for the failure path.

**Idiomatic call shape:**
```csharp
string result = await CredentialService.StoreCredentialAsync("openai", "ApiKey", key);
if (result.StartsWith("ERRO"))
{
    ErrorLabel.Text = $"Erro ao salvar: {result}";
    ErrorLabel.Visibility = Visibility.Visible;
    return;
}
```

### Pattern: Settings Load → Mutate → Save (D-11 migration + D-06 sentinel write)

**Source:** `AIBWindows/Services/SettingsService.cs:61-100` + existing call sites at `SettingsWindow.xaml.cs:120-141` and `App.xaml.cs:33`.
**Apply to:** `App.xaml.cs::OnStartup` (D-11 migration) AND `FirstRunWindow.xaml.cs::Save_Click` (both branches).

**Contract:**
- `LoadSettings()` returns a non-null `UserAppSettings` (defaults if file missing or decrypt fails — see lines 65, 77-90).
- `SaveSettings(settings)` writes DPAPI-encrypted JSON to `~/.AIB/profile.dat` (line 99 `File.WriteAllBytes`).
- Both calls are synchronous on the UI thread (acceptable — `profile.dat` is small, write is atomic at the OS level for small files; RESEARCH Pitfall 3).

**Idiomatic mutation:**
```csharp
var settings = _settingsService.LoadSettings();
settings.ApiKey = "use-vault";          // mutate one or more fields
_settingsService.SaveSettings(settings);
```

### Pattern: Ollama models fetch (D-09)

**Source:** `AIBWindows/Services/SettingsService.cs:102-132`
**Apply to:** `FirstRunWindow.xaml.cs::RefreshModelsAsync` (Ollama branch)

**Contract:**
- Returns `List<string>` of model names on success.
- Returns empty `List<string>` on any failure (network, parse, no Ollama running) — line 128-130 `catch { return new List<string>(); }`. Does NOT throw.
- Caller branches on `models.Any()` to distinguish loaded vs error.

**Idiomatic call shape (matches `SettingsWindow.xaml.cs:91-113`):**
```csharp
private async System.Threading.Tasks.Task RefreshModelsAsync()
{
    LoadingProgress.Visibility = Visibility.Visible;
    try
    {
        var models = await _settingsService.GetOllamaModelsAsync(apiUrl);
        if (models.Any())
        {
            ModelComboBox.ItemsSource = models;
        }
        else
        {
            // UI-SPEC state S3: show OllamaErrorBlock with Retry + fallback link
            OllamaErrorBlock.Visibility = Visibility.Visible;
            ModelComboBox.Visibility = Visibility.Collapsed;
        }
    }
    finally
    {
        LoadingProgress.Visibility = Visibility.Collapsed;
    }
}
```

### Pattern: Audit Log (D-04 + D-11 + Save outcomes)

**Source:** `AIBWindows/Services/AuditLogService.cs:34-61` + Phase 1 call sites at `NativeTools.cs:344, 352, 379, 386, 390`.
**Apply to:** `FirstRunWindow.xaml.cs::Save_Click` (`firstrun_saved`, `firstrun_invalid_key`), `App.xaml.cs::ShowFirstRunWindow` cancel branch (`firstrun_cancelled`), `App.xaml.cs::OnStartup` migration block (`migration_clear_apikey`).

**Contract:**
- Fire-and-forget: `_ = AuditLogService.AppendAsync(entry);`
- Accepts any `object` (anonymous types preferred for inline schema).
- Never throws — failures go to `Console.WriteLine` with `[AUDIT]` tag (line 59).
- `SemaphoreSlim`-guarded JSONL append (one JSON object per line, terminated `\n`).

**Idiomatic call shape (matches `NativeTools.cs:344` etc.):**
```csharp
_ = AuditLogService.AppendAsync(new
{
    ts = DateTime.UtcNow.ToString("o"),
    outcome = "firstrun_saved",
    provider = "OpenAI",
    key_last4 = key.Length >= 4 ? key[^4..] : "----"
});
```

**Schema rule (RESEARCH §Audit-log Schema):** `ts` (ISO 8601 UTC) + `outcome` are required on every entry; other fields are optional. **NEVER include the full key** — `key_last4` is the canonical fingerprint field.

### Pattern: Sentinel-string for "fetch from elsewhere"

**Source:** Phase 2 introduction (RESEARCH §Pattern 1). Reuses existing `settings.ApiKey` field as a sentinel ("use-vault", "ollama", "placeholder") rather than the actual secret.
**Apply to:** `FirstRunWindow.xaml.cs::Save_Click`, `App.xaml.cs::OnStartup` migration, both `OpenAIService.cs` resolution sites.

**Why:** Single source of truth for *configuration* (the existing DPAPI-encrypted `profile.dat`) without storing the *secret* there. The vault is the secret store. No double-write, no sync bugs. Existing `profile.dat` schema unchanged (NFR-04 compliance).

**Values:**
- `"use-vault"` — sentinel meaning "consult `CredentialService.RetrieveCredential("openai", "ApiKey")` at runtime"
- `"ollama"` — literal, used for Ollama provider (matches existing default at `SettingsService.cs:15`)
- `"placeholder"` — fallback when vault returns `ERRO`; OpenAI SDK will fail with auth error (existing convention at `OpenAIService.cs:667, 716`)

### Pattern: DialogResult lifecycle (D-04 + D-08)

**Source:** `AIBWindows/Views/CommandConfirmationWindow.xaml.cs:21-34`
**Apply to:** `FirstRunWindow.xaml.cs::Save_Click` (DialogResult=true) and `Cancel_Click` (DialogResult=false). Parent `App.xaml.cs::ShowFirstRunWindow` checks `bool? ok = win.ShowDialog()` and routes:
- `ok == true` → `_chatWindow?.ToggleWindow()` (D-08 sequencing)
- else → audit `firstrun_cancelled` + `Application.Current.Shutdown()` (D-04, on UI thread — Pitfall 6 mitigation)

**Why parent handles Shutdown, not the window itself:** Pitfall 6 — calling `Shutdown()` from the window's own Cancel handler races with the window's own close path and may leak the tray icon. The parent thread guarantees UI-thread Shutdown.

---

## No Analog Found

| File | Role | Data Flow | Reason |
|------|------|-----------|--------|
| `.env.example` | config doc | static template | Doc-only file; existing `.env` files contain the placeholder shape but are the deletion target, not an analog. Planner copies the literal text block from CONTEXT.md D-10. |
| `.planning/phases/02-key-rotation-env-hardening/evidence/.gitkeep` | filesystem marker | n/a | Zero-byte placeholder; no analog needed. |

---

## Metadata

**Analog search scope:**
- `AIBWindows/Views/` (all `.xaml` and `.xaml.cs` — found 4: ChatWindow, SettingsWindow, CommandConfirmationWindow, ContextSidebar)
- `AIBWindows/Services/` (all `.cs` — verified CredentialService, SettingsService, AuditLogService, OpenAIService, DirectoryService, NativeTools)
- `AIBWindows/` root (App.xaml.cs, App.xaml, AIB.csproj)
- Repo root (.env, .env.example absence, .gitignore, README.md)
- `Regras de Identidade/` (SEGURANCA.MD)

**Files scanned:** 14 files Read in full or in load-bearing sections; 4 Globs + 2 Greps for analog discovery.

**Pattern extraction date:** 2026-05-30

**Confidence:** HIGH — every code excerpt above is verbatim from a verified Read. No invented patterns. The phase is "wire-up" work over an already-correct service layer.

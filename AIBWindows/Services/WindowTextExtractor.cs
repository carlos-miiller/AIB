using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace AIB.Services;

/// <summary>
/// Extrai texto de uma janela via UI Automation (mesmo framework usado por screen readers
/// como NVDA/JAWS). Muito mais barato e limpo que OCR:
///   - Navegadores: pega só o conteúdo do Document, sem abas/favoritos/URL.
///   - Editores: pega só o documento aberto.
///   - Apps em geral: caminha pela árvore e coleta Name de elementos textuais.
///
/// Custos típicos: 50-300ms, &lt; 15 MB de pico. Compare com OCR: 1-2s, 200-400 MB.
///
/// Retorna null quando a janela não expõe UIA útil (jogos, viewers de PDF antigos,
/// alguns Electron "fora do padrão"). O chamador decide se usa fallback OCR ou desiste.
/// </summary>
public static class WindowTextExtractor
{
    private const int DEFAULT_TIMEOUT_MS = 800;
    private const int MAX_CHARS = 4000;     // hard cap antes de devolver pro chamador
    private const int MAX_DEPTH = 8;        // profundidade do walker
    private const int MIN_TEXT_LEN = 30;    // mínimo para considerar "útil"

    /// <summary>
    /// Tenta extrair texto significativo da janela. Retorna null se UIA falhar,
    /// timeoutar ou não devolver nada útil (&lt; MIN_TEXT_LEN chars).
    /// </summary>
    public static async Task<string?> ExtractTextAsync(IntPtr hwnd, int timeoutMs = DEFAULT_TIMEOUT_MS)
    {
        if (hwnd == IntPtr.Zero) return null;

        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            return await Task.Run(() => ExtractTextSync(hwnd, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"[UIA] Timeout extraindo texto de hwnd={hwnd} ({timeoutMs}ms)");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[UIA] Erro: {ex.Message}");
            return null;
        }
    }

    private static string? ExtractTextSync(IntPtr hwnd, CancellationToken ct)
    {
        AutomationElement? root;
        try { root = AutomationElement.FromHandle(hwnd); }
        catch { return null; }
        if (root == null) return null;

        // Estratégia 1: TextPattern direto na raiz (Word, Notepad, alguns editores)
        ct.ThrowIfCancellationRequested();
        var fromRootPattern = TryExtractViaTextPattern(root);
        if (IsUseful(fromRootPattern)) return Truncate(fromRootPattern!);

        // Estratégia 2: Procurar Document (navegadores, PDF, Office Web Components)
        ct.ThrowIfCancellationRequested();
        AutomationElement? doc = null;
        try
        {
            doc = root.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
        }
        catch { /* árvore pode ter mudado */ }

        if (doc != null)
        {
            ct.ThrowIfCancellationRequested();
            var fromDocPattern = TryExtractViaTextPattern(doc);
            if (IsUseful(fromDocPattern)) return Truncate(fromDocPattern!);

            // Walker dentro do Document — limita o ruído de chrome do app
            var walkedDoc = WalkAndCollect(doc, ct);
            if (IsUseful(walkedDoc)) return Truncate(walkedDoc);
        }

        // Estratégia 3: Walker da árvore inteira (último recurso, mais ruído)
        ct.ThrowIfCancellationRequested();
        var fullWalk = WalkAndCollect(root, ct);
        return IsUseful(fullWalk) ? Truncate(fullWalk) : null;
    }

    // ── Extração via TextPattern (o ideal quando disponível) ─────────────────
    private static string? TryExtractViaTextPattern(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var raw) && raw is TextPattern tp)
            {
                return tp.DocumentRange.GetText(MAX_CHARS);
            }
        }
        catch { /* elemento volátil */ }
        return null;
    }

    // ── Walker recursivo coletando Names ─────────────────────────────────────
    private static string WalkAndCollect(AutomationElement root, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            WalkRecursive(root, sb, seen, ct, depth: 0);
        }
        catch (OperationCanceledException) { /* devolve o que já temos */ }
        return sb.ToString();
    }

    private static void WalkRecursive(AutomationElement el, StringBuilder sb, HashSet<string> seen, CancellationToken ct, int depth)
    {
        if (depth > MAX_DEPTH || sb.Length >= MAX_CHARS) return;
        ct.ThrowIfCancellationRequested();

        try
        {
            string name = el.Current.Name ?? string.Empty;
            if (name.Length >= 3 && name.Length <= 500 && !LooksLikeUiNoise(name) && seen.Add(name))
            {
                sb.AppendLine(name);
            }

            AutomationElementCollection children;
            try { children = el.FindAll(TreeScope.Children, Condition.TrueCondition); }
            catch { return; }

            foreach (AutomationElement child in children)
            {
                if (sb.Length >= MAX_CHARS) break;
                if (ct.IsCancellationRequested) break;
                WalkRecursive(child, sb, seen, ct, depth + 1);
            }
        }
        catch { /* elemento sumiu durante o walk; tudo bem */ }
    }

    /// <summary>
    /// Heurística leve para descartar entradas de UI puro: botões nominais,
    /// rótulos de menu, etc. Sem isso, o walker traz "File, Edit, View, Help"
    /// de toda app.
    /// </summary>
    private static bool LooksLikeUiNoise(string s)
    {
        if (s.Length < 4) return true;
        // Strings que são só símbolos/setas de UI
        if (s.Length <= 3 && !ContainsLetter(s)) return true;
        return false;
    }

    private static bool ContainsLetter(string s)
    {
        foreach (var c in s) if (char.IsLetter(c)) return true;
        return false;
    }

    private static bool IsUseful(string? text)
        => !string.IsNullOrWhiteSpace(text) && text!.Length >= MIN_TEXT_LEN;

    private static string Truncate(string text)
        => text.Length > MAX_CHARS ? text.Substring(0, MAX_CHARS) : text;
}

using System;
using System.Collections.ObjectModel;

namespace AIB.Services;

/// <summary>
/// Item do histórico do Shadow Assistant: o que foi sugerido e em qual janela.
/// Imutável; cada disparo cria uma instância nova adicionada no topo da coleção.
/// </summary>
public class ShadowSuggestion
{
    public DateTime Timestamp { get; init; }
    public string WindowTitle { get; init; } = "";
    public string Text { get; init; } = "";
}

/// <summary>
/// Buffer circular em memória das últimas N sugestões do Shadow.
/// Usado pela sidebar (aba "Shadow Ast") para listar o que a IA já comentou.
///
/// Não persiste em disco — quando o app fecha, o histórico zera.
/// Se precisar persistir, plugue um Save/Load em ChatHistoryService.
/// </summary>
public static class ShadowHistoryService
{
    private const int MAX_ENTRIES = 5;

    /// <summary>
    /// Coleção observável: a sidebar pode bindar diretamente e atualizar a UI
    /// automaticamente sem refresh manual.
    /// </summary>
    public static ObservableCollection<ShadowSuggestion> Suggestions { get; } = new();

    public static void Add(string windowTitle, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var entry = new ShadowSuggestion
        {
            Timestamp = DateTime.Now,
            WindowTitle = string.IsNullOrWhiteSpace(windowTitle) ? "[sem título]" : windowTitle,
            Text = text
        };

        // ObservableCollection precisa ser modificada na UI thread, senão WPF lança.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            InsertEntry(entry);
        }
        else
        {
            dispatcher.Invoke(() => InsertEntry(entry));
        }
    }

    private static void InsertEntry(ShadowSuggestion entry)
    {
        Suggestions.Insert(0, entry);
        while (Suggestions.Count > MAX_ENTRIES)
            Suggestions.RemoveAt(Suggestions.Count - 1);
    }
}

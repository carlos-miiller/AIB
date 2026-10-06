using Markdig;
using Markdig.Wpf;

namespace AIB.Ui;

/// <summary>
/// O interpretador de Markdown das bolhas da IA.
/// <para>
/// Existe por causa de uma regra do Markdown que atrapalha aqui: uma quebra de linha simples
/// NÃO quebra a linha na saída. O texto
/// </para>
/// <code>
/// Li o arquivo.
/// Faltam dois itens.
/// </code>
/// <para>
/// vira um parágrafo só — "Li o arquivo. Faltam dois itens." — porque para o Markdown só linha
/// em branco separa parágrafo. É o comportamento correto para um documento escrito à mão, e o
/// errado para uma conversa: o modelo escreve com quebras simples, e a resposta chegava na
/// tela como um bloco corrido.
/// </para>
/// <para>
/// <c>UseSoftlineBreakAsHardlineBreak</c> faz a quebra simples valer. O resto da configuração é
/// a do Markdig.Wpf: só as extensões que o renderizador de WPF sabe desenhar — pedir extensões
/// que ele não conhece produz documento com nós que ele ignora em silêncio.
/// </para>
/// </summary>
public static class MarkdownPipelines
{
    /// <summary>
    /// Um só para todas as bolhas: o pipeline é imutável depois de construído e montar um por
    /// mensagem seria refazer o mesmo trabalho a cada resposta.
    /// </summary>
    public static MarkdownPipeline Conversa { get; } =
        new MarkdownPipelineBuilder()
            .UseSupportedExtensions()
            .UseSoftlineBreakAsHardlineBreak()
            .Build();
}

namespace AIB.Services;

/// <summary>
/// Unidade do fluxo devolvido por <see cref="ConversationService.StreamResponseAsync"/>.
/// <para>
/// A fronteira entre falas viaja EM BANDA, junto com o texto, e não por callback. Um callback
/// atravessa <c>SynchronizationContext.Post</c>, que é assíncrono: a fronteira poderia executar
/// depois que o texto da fala seguinte já tivesse sido acumulado, e a fala terminaria no balão
/// errado. Aqui a ordem é garantida por construção — o consumidor lê os itens na ordem exata em
/// que o laço ReAct os produziu.
/// </para>
/// </summary>
public abstract record ChatStreamItem
{
    private ChatStreamItem() { }

    /// <summary>Pedaço de texto do canal final.</summary>
    public sealed record Text(string Value) : ChatStreamItem;

    /// <summary>
    /// A fala corrente terminou e o agente partiu para uma ferramenta. O que veio até aqui é
    /// uma mensagem completa; o que vier depois é a próxima.
    /// </summary>
    public sealed record SegmentBreak(int Iteration) : ChatStreamItem;

    /// <summary>
    /// Uma ferramenta entrou em execução — o chip em curso da cadeia de ações (§4.2).
    /// <para>
    /// Viaja em banda pelo mesmo motivo do <see cref="SegmentBreak"/>: a ordem em relação ao
    /// texto importa. Um chip anunciado por callback poderia aparecer depois do balão da fala
    /// seguinte, invertendo a narrativa do turno na tela.
    /// </para>
    /// </summary>
    /// <param name="Id">Id da tool call, que casa o começo com o fim.</param>
    /// <param name="Tool">Nome da ferramenta.</param>
    /// <param name="Argument">Resumo curto do argumento; vazio quando não há um bom resumo.</param>
    public sealed record ToolStarted(string Id, string Tool, string Argument) : ChatStreamItem;

    /// <summary>
    /// Uma ferramenta terminou — o chip colapsa em ícone, ou vira erro (§4.3 e §4.7).
    /// </summary>
    /// <param name="Id">Mesmo id do <see cref="ToolStarted"/> correspondente.</param>
    /// <param name="Failed">Se falhou ou foi recusada pelo usuário.</param>
    /// <param name="Denied">Se o motivo foi recusa no portão de confirmação.</param>
    /// <param name="Artifact">Literal preservado, quando a ferramenta tem um.</param>
    /// <param name="Detail">Primeira linha do erro, quando falhou.</param>
    public sealed record ToolFinished(
        string Id,
        bool Failed,
        bool Denied,
        Memory.Artifact? Artifact,
        string? Detail) : ChatStreamItem;
}

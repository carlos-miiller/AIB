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
}

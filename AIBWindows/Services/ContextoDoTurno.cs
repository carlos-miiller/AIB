using System.Threading;

namespace AIB.Services;

/// <summary>
/// De onde vem o turno que está executando uma ferramenta: de que conversa de e-mail (se for
/// uma) e se é a conversa do orbe (o Shadow).
/// <para>
/// Existe porque o registro de ferramentas e o portão são UM SÓ para as duas conversas, a da
/// janela e a do orbe. O lembrete precisa saber de que e-mail nasceu (para o aviso oferecer
/// "Abrir o e-mail") e a confirmação, se o pedido veio do Shadow (para o selo e para "Abrir no
/// Shadow").
/// </para>
/// <para>
/// <see cref="AsyncLocal{T}"/>: o <c>AgentLoop</c> põe o valor antes de chamar o registro, e
/// ele segue pelos <c>await</c> até a ferramenta e o portão — mesmo com várias ferramentas em
/// paralelo, cada uma vê o do seu turno. Fora de um turno, é nulo.
/// </para>
/// </summary>
/// <param name="ChaveDoEmail">A chave da conversa de e-mail (<c>conta|thr:id</c>), ou vazio.</param>
/// <param name="Shadow">O turno é da conversa do orbe.</param>
public sealed record ContextoDoTurno(string ChaveDoEmail, bool Shadow)
{
    private static readonly AsyncLocal<ContextoDoTurno?> _atual = new();

    public static ContextoDoTurno? Atual
    {
        get => _atual.Value;
        set => _atual.Value = value;
    }
}

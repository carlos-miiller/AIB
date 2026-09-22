using System;
using System.Threading.Tasks;

namespace AIB.Services;

/// <summary>
/// Portão humano que pergunta DENTRO da conversa (§5.3), em vez de abrir uma janela modal.
/// <para>
/// Continua sendo um <see cref="IConfirmationPrompt"/>: o <see cref="ToolRegistry"/> não sabe
/// nem precisa saber se a pergunta virou card ou janela. Toda a lógica do portão — quem passa,
/// quem é barrado antes de perguntar, o "sempre permitir" por comando — segue onde estava.
/// </para>
/// <para>
/// O apresentador é registrado pela janela de chat quando ela nasce. Enquanto não houver um,
/// ou se ele falhar, a resposta é RECUSA: é o mesmo padrão seguro de antes — sem alguém para
/// autorizar, nada executa.
/// </para>
/// </summary>
public sealed class ChatConfirmationPrompt : IConfirmationPrompt
{
    private readonly object _trava = new();
    private Func<CommandConfirmationContext, Task<(bool Allowed, bool AlwaysAllow)>>? _apresentar;

    /// <summary>
    /// Liga o portão à interface. Chamado pela janela de chat; passar <c>null</c> desliga e
    /// volta ao padrão de recusa.
    /// </summary>
    public void Conectar(Func<CommandConfirmationContext, Task<(bool, bool)>>? apresentador)
    {
        lock (_trava) _apresentar = apresentador;
    }

    public async Task<(bool Allowed, bool AlwaysAllow)> AskAsync(CommandConfirmationContext context)
    {
        var resposta = await PerguntarAsync(context).ConfigureAwait(false);
        return (resposta.Allowed, resposta.AlwaysAllow);
    }

    public async Task<RespostaDoPortao> PerguntarAsync(CommandConfirmationContext context)
    {
        Func<CommandConfirmationContext, Task<(bool, bool)>>? apresentar;
        lock (_trava) apresentar = _apresentar;

        if (apresentar == null)
        {
            Console.WriteLine("[PORTAO] Sem interface para perguntar. Ação recusada por padrão.");
            return new RespostaDoPortao(false, false, "não há interface disponível para perguntar");
        }

        try
        {
            var (permitido, sempre) = await apresentar(context).ConfigureAwait(false);
            return new RespostaDoPortao(permitido, sempre, null);
        }
        catch (Exception ex)
        {
            // Falha ao MOSTRAR a pergunta não pode virar autorização silenciosa — nem recusa do
            // usuário, que não viu cartão nenhum.
            Console.WriteLine($"[PORTAO] Falha ao exibir a confirmação: {ex.Message}. Ação recusada.");
            return new RespostaDoPortao(false, false, "o cartão de confirmação não pôde ser mostrado");
        }
    }
}

using System.Threading.Tasks;

namespace AIB.Services;

/// <summary>
/// Portão humano para ferramentas destrutivas. Existe como interface — e não como chamada
/// direta a uma janela — para que o <see cref="ToolRegistry"/> não dependa de WPF e o portão
/// possa ser exercitado em teste.
/// <para>
/// O gate anterior era código morto: a janela de confirmação existia completa e não tinha um
/// único chamador em toda a árvore. Amarrar o registry a um tipo de UI foi o que tornou
/// impossível testar se o portão era realmente acionado, e por isso ninguém percebeu quando ele
/// sumiu.
/// </para>
/// <para>
/// A implementação viva é a <see cref="ChatConfirmationPrompt"/>, que pergunta dentro da própria
/// conversa (§5.3 da spec de chat). A janela modal que existia antes foi REMOVIDA junto com a
/// troca, e não deixada de lado: uma segunda porta de confirmação sem chamador é exatamente o
/// que este comentário descreve — parece proteção, e não é.
/// </para>
/// </summary>
public interface IConfirmationPrompt
{
    /// <summary>
    /// Pergunta ao usuário. <c>Allowed</c> falso significa recusa — inclusive quando não há UI
    /// disponível, que é o padrão seguro: sem alguém para autorizar, nada executa.
    /// <c>AlwaysAllow</c> pede para não perguntar de novo por este mesmo comando na sessão.
    /// </summary>
    Task<(bool Allowed, bool AlwaysAllow)> AskAsync(CommandConfirmationContext context);

    /// <summary>
    /// A mesma pergunta, dizendo também quando NINGUÉM respondeu. É o que o registry usa: sem
    /// isso, falta de interface e falha ao mostrar o cartão voltavam como recusa, a auditoria
    /// gravava <c>deny_usuario</c> e o modelo ouvia que o usuário tinha dito não — e reagia a uma
    /// decisão que ninguém tomou.
    /// </summary>
    async Task<RespostaDoPortao> PerguntarAsync(CommandConfirmationContext context)
    {
        var (permitido, sempre) = await AskAsync(context).ConfigureAwait(false);
        return new RespostaDoPortao(permitido, sempre, null);
    }
}

/// <summary>O que o portão humano devolveu.</summary>
/// <param name="Allowed">Autorizado. Falso em toda resposta que não seja um "Permitir" explícito.</param>
/// <param name="AlwaysAllow">Não perguntar de novo por este comando na sessão.</param>
/// <param name="SemResposta">
/// Preenchido quando ninguém respondeu — não havia interface, ou mostrar o cartão falhou. Nulo
/// quando houve um clique, qualquer que tenha sido.
/// </param>
public readonly record struct RespostaDoPortao(bool Allowed, bool AlwaysAllow, string? SemResposta);

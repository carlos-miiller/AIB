using System;

namespace AIB.Services;

public static class LevelService
{
    // Limiares de XP (Mensagens)
    private static readonly int[] XPThresholds = { 0, 20, 50, 100, 200, 350, 600, 1000, 1500 };

    public static int GetLevel(int xp)
    {
        for (int i = XPThresholds.Length - 1; i >= 0; i--)
        {
            if (xp >= XPThresholds[i])
                return i + 1;
        }
        return 1;
    }

    public static int GetXPForNextLevel(int currentLevel)
    {
        // No teto não existe limiar seguinte no array. Devolver XPThresholds[8] devolveria o limiar
        // do PRÓPRIO nível 9 — igual ao de GetXPForCurrentLevel(9) — zerando o denominador da barra
        // de progresso e fazendo o tooltip exibir coisas como "3000/1500 XP". Extrapola o último
        // degrau para manter um teto real e maior que o piso.
        if (currentLevel >= 9) return XPThresholds[8] + (XPThresholds[8] - XPThresholds[7]);
        return XPThresholds[currentLevel]; // currentLevel (1-indexed) already points to the next threshold index
    }

    public static int GetXPForCurrentLevel(int currentLevel)
    {
        if (currentLevel < 1) return 0;
        return XPThresholds[currentLevel - 1];
    }

    /// <summary>
    /// Orçamento de histórico por nível, em tokens. DERIVADO da janela, nunca digitado.
    /// <para>
    /// Nível 1 recebe um quarto da janela e nível 9 recebe três quartos, em oito passos iguais.
    /// Com <c>num_ctx</c> em 32768 isso dá 8192 no piso — o mesmo de antes, escolhido para caber
    /// uma alma grande com folga de conversa (o SOUL.MD da Ayano tem ~3900 tokens) — e 24576 no
    /// topo.
    /// </para>
    /// <para>
    /// ERA UMA TABELA DE LITERAIS, de 8192 a 12288, e o comentário dela dizia "teto amarrado ao
    /// num_ctx de 16384". A janela subiu para 32768 e a tabela ficou: o nível 9 passou a usar
    /// 37% do que o modelo aguenta, e os oito degraus de +512 eram invisíveis — chegar ao topo
    /// rendia 4096 tokens, metade de um nível 1. A dépendência estava ESCRITA e mesmo assim
    /// quebrou em silêncio, que é o que uma constante anotada faz quando a outra ponta se mexe.
    /// Derivar é o que faz a próxima mudança de janela levar a escala junto.
    /// </para>
    /// <para>
    /// O TETO DE 75% não é folclore: a geração também consome a janela, e
    /// <c>ConversationService.TetoDaPoda</c> é <c>num_ctx - 2048</c>. Em qualquer janela maior
    /// que 8192, três quartos ficam abaixo dele com margem — e é por isso que este teto pode
    /// ser proporcional em vez de mais uma constante para envelhecer.
    /// </para>
    /// <para>
    /// Subir a janela sobe a escala junto, e isso é desejado; o que NÃO pode é mudar
    /// <c>num_ctx</c> no meio da sessão. Qualquer requisição com num_ctx diferente faz o Ollama
    /// recarregar o runner e descartar o KV cache — medido: prefill de 343ms volta a 204s.
    /// </para>
    /// </summary>
    public static int GetMaxTokensForLevel(int level) =>
        GetMaxTokensForLevel(level, Ai.ChatRequestOptions.Default.NumCtx);

    /// <summary>
    /// A mesma conta sobre uma janela DADA — a que está digitada na tela de configurações, antes
    /// de salvar. Existe para a tela não precisar de uma cópia da conta.
    /// </summary>
    public static int GetMaxTokensForLevel(int level, int janela)
    {
        int piso = janela / 4;
        int teto = janela * 3 / 4;
        int passo = (teto - piso) / (NivelMaximo - 1);

        int n = Math.Clamp(level, 1, NivelMaximo);

        return piso + (n - 1) * passo;
    }

    /// <summary>O último nível. Nível acima disso recebe o mesmo orçamento.</summary>
    public const int NivelMaximo = 9;
}

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
    /// Orçamento de histórico por nível, em tokens: de <see cref="Piso"/> no nível 1 a
    /// <see cref="Topo"/> no nível 9, multiplicando pelo mesmo fator a cada nível (~1,344), e
    /// nunca acima de três quartos da janela do modelo.
    /// <para>
    /// ERA DERIVADO DA JANELA, de um quarto a três quartos em passos iguais. Com a janela de
    /// 132.768 do Gemini, isso dava 33.192 no nível 1 e 99.576 no 9: subir de nível rendia
    /// 8.298 tokens por degrau, e o nível 1 já começava num terço de cem mil. O usuário pediu
    /// mais distância entre os níveis (27/10/2026): a escala agora é geométrica, os primeiros
    /// degraus pequenos e os últimos grandes, e o número não muda com o modelo.
    /// </para>
    /// <para>
    /// O TETO DE 75% DA JANELA continua: a geração também consome a janela, e
    /// <c>ConversationService.TetoDaPoda</c> é <c>num_ctx - 2048</c>. Num modelo local de
    /// 32.768, o nível 1 (24.000) fica logo abaixo dele (24.576) e todos os outros param ali.
    /// Para o nível 9 valer inteiro, a janela precisa de 341.334 tokens.
    /// </para>
    /// <para>
    /// O que NÃO pode é mudar <c>num_ctx</c> no meio da sessão. Qualquer requisição com
    /// num_ctx diferente faz o Ollama recarregar o runner e descartar o KV cache — medido:
    /// prefill de 343ms volta a 204s.
    /// </para>
    /// </summary>
    public static int GetMaxTokensForLevel(int level) =>
        GetMaxTokensForLevel(level, Ai.ChatRequestOptions.Default.NumCtx);

    /// <summary>
    /// A mesma conta sobre uma janela DADA — a que está digitada na tela de configurações, antes
    /// de salvar. Existe para a tela não precisar de uma cópia da conta.
    /// </summary>
    public static int GetMaxTokensForLevel(int level, int janela) =>
        Math.Min(Escala(level), janela * 3 / 4);

    /// <summary>O orçamento do nível sem o limite da janela.</summary>
    public static int Escala(int level)
    {
        int n = Math.Clamp(level, 1, NivelMaximo);
        double fator = Math.Pow((double)Topo / Piso, (n - 1) / (double)(NivelMaximo - 1));
        return (int)Math.Round(Piso * fator);
    }

    /// <summary>Orçamento do nível 1.</summary>
    public const int Piso = 24_000;

    /// <summary>Orçamento do último nível.</summary>
    public const int Topo = 256_000;

    /// <summary>O último nível. Nível acima disso recebe o mesmo orçamento.</summary>
    public const int NivelMaximo = 9;
}

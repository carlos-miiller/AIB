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
    /// Orçamento de histórico por nível, em tokens.
    /// <para>
    /// Teto amarrado ao <c>num_ctx</c> de 16384 pedido ao Ollama: o orçamento é do PROMPT, e a
    /// geração também consome a janela. Reservamos 4096 para a resposta, então o topo é 12288.
    /// A escala anterior ia a 53248 — 3,25× a janela real. O Ollama truncava o prompt pela
    /// frente, arrancando o system prompt que o hard-lock do Trim existe para proteger.
    /// </para>
    /// <para>
    /// Piso de 8192 (era 3072) para caber uma alma grande com folga de conversa: o SOUL.MD da
    /// Ayano sozinho tem ~3485 tokens e não cabia no nível 1, deixando o usuário estourado desde
    /// a primeira mensagem sem que nenhuma poda pudesse recuperar.
    /// </para>
    /// <para>
    /// Subir esta escala exige subir o <c>num_ctx</c> junto — e ele precisa ser CONSTANTE na
    /// sessão inteira: qualquer requisição com num_ctx diferente faz o Ollama recarregar o
    /// runner e descartar o KV cache (medido: prefill de 343ms volta a 204s).
    /// </para>
    /// </summary>
    public static int GetMaxTokensForLevel(int level)
    {
        return level switch
        {
            1 => 8192,
            2 => 8704,
            3 => 9216,
            4 => 9728,
            5 => 10240,
            6 => 10752,
            7 => 11264,
            8 => 11776,
            _ => 12288 // Level 9+
        };
    }
}

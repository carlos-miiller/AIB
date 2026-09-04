using System;

namespace AIB.Services.Memory;

/// <summary>
/// Cotas em tokens para cada faixa da memória e para a conversa viva.
/// </summary>
/// <param name="Facts">Fatos duráveis (nível 2 destilado).</param>
/// <param name="Acts">Atos (resumo de capítulos).</param>
/// <param name="Chapters">Capítulos recentes.</param>
/// <param name="Live">Mensagens cruas ainda no contexto.</param>
public readonly record struct MemoryQuota(int Facts, int Acts, int Chapters, int Live)
{
    /// <summary>Soma das três faixas de memória. Não inclui a conversa viva.</summary>
    public int Memory => Facts + Acts + Chapters;

    /// <summary>Memória desligada: não sobrou espaço para ela sem sufocar a conversa.</summary>
    public bool IsOff => Memory == 0;
}

/// <summary>
/// Reparte o orçamento do nível entre memória e conversa. Função pura.
/// <para>
/// Alocação FIXA não sobrevive à conta real: a alma da Ayano tem ~3.900 tokens e o nível 1
/// tem 8.192 no total, sobrando 4.292 para tudo. Reservar 2.400 fixos de memória deixaria
/// 1.892 para conversar — inutilizável. Por isso a repartição é proporcional ao que sobra
/// DEPOIS do prefixo fixo, e desliga quando o que sobra é pequeno demais.
/// </para>
/// </summary>
public static class MemoryBudget
{
    /// <summary>
    /// Abaixo disto a memória é desligada por inteiro. Um agente sem memória funciona; um
    /// agente sem espaço para conversar, não.
    /// </summary>
    public const int MinimumAvailable = 2000;

    /// <summary>
    /// Fatia do que sobra destinada à memória — o PADRÃO. Virou configurável porque numa
    /// máquina de 3 tok/s a diferença entre 0,25 e 0,15 é a diferença entre esperar minutos
    /// por uma compactação e não esperar.
    /// </summary>
    public const double MemoryFraction = UserAppSettings.PadraoDaFatiaDeMemoria;

    private const double FactsShare = 0.20;
    private const double ActsShare = 0.30;
    private const double ChaptersShare = 0.50;

    /// <summary>
    /// Fração da cota viva a partir da qual vale compactar. Não é 100% de propósito: o gatilho
    /// precisa disparar ANTES do estouro, senão a poda de emergência entra primeiro e come as
    /// mensagens que o capítulo iria resumir.
    /// </summary>
    public const double CompactionTrigger = UserAppSettings.PadraoDoGatilhoDeCompactacao;

    /// <param name="levelBudget">Teto de tokens do nível do usuário.</param>
    /// <param name="fixedPrefixTokens">SOUL + prompt base: o que existe antes de qualquer conversa.</param>
    /// <param name="memoryFraction">
    /// Quanto do que sobra vai para memória. Parâmetro com padrão, e não leitura de
    /// configuração aqui dentro: esta classe é aritmética pura, e é o que permite os ensaios
    /// dela não precisarem de disco nem de DPAPI.
    /// </param>
    public static MemoryQuota Compute(
        int levelBudget, int fixedPrefixTokens, double memoryFraction = MemoryFraction)
    {
        int disponivel = levelBudget - Math.Max(0, fixedPrefixTokens);

        // Prefixo fixo maior que o orçamento inteiro (alma enorme em nível baixo): não há o que
        // repartir. Devolve tudo zerado e deixa a poda de emergência cuidar do resto.
        if (disponivel <= 0) return new MemoryQuota(0, 0, 0, 0);

        if (disponivel < MinimumAvailable)
            return new MemoryQuota(0, 0, 0, disponivel);

        int memoria = (int)(disponivel * memoryFraction);

        int fatos = (int)(memoria * FactsShare);
        int atos = (int)(memoria * ActsShare);

        // Capítulos recebem o resto, e não a sua fração arredondada: assim as três faixas
        // somam exatamente 'memoria' e nenhum token some no arredondamento.
        int capitulos = memoria - fatos - atos;

        return new MemoryQuota(fatos, atos, capitulos, disponivel - memoria);
    }

    /// <summary>Tokens de conversa viva a partir dos quais compensa compactar.</summary>
    public static int CompactionThreshold(MemoryQuota quota, double gatilho = CompactionTrigger) =>
        (int)(quota.Live * gatilho);
}

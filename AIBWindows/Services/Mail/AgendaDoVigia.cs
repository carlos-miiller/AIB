using System;
using System.Collections.Generic;
using System.Linq;

namespace AIB.Services.Mail;

/// <summary>
/// Quando o vigia trabalha.
/// <para>
/// Dois ritmos, e a diferença entre eles é o que decide o custo: a SONDAGEM roda de 20 em 20
/// minutos e é só código — conta, agrupa, compara. O DIGEST roda três vezes por dia e é o
/// único momento em que um modelo é acordado. Inverter isso, acordando o 9B a cada sondagem,
/// deixaria a máquina ocupada o dia inteiro para, no estado estável, não encontrar nada.
/// </para>
/// <para>
/// Tudo aqui é função pura sobre um relógio recebido. Um agendador que lê
/// <c>DateTime.Now</c> por dentro só se testa esperando o relógio da máquina virar.
/// </para>
/// </summary>
public static class AgendaDoVigia
{
    /// <summary>
    /// Os horários do digest — §Os Três Laços. Ficam ANTES das janelas naturais de leitura:
    /// antes do começo do dia, antes do almoço e antes do fim da tarde. Um digest às 13h05 é
    /// um digest que chega depois de o usuário já ter aberto a caixa por conta própria.
    /// </summary>
    public static readonly TimeSpan[] Horarios =
    {
        new(8, 25, 0),
        new(12, 55, 0),
        new(16, 55, 0)
    };

    /// <summary>De quanto em quanto tempo o código olha a caixa sem acordar modelo nenhum.</summary>
    public static readonly TimeSpan IntervaloDaSondagem = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Se está na hora de um digest.
    /// <para>
    /// A comparação é "passou do horário e ainda não rodou depois dele", e não "o relógio marca
    /// exatamente 8:25". A máquina pode estar suspensa, desligada ou o laço pode ter perdido o
    /// minuto exato — e um digest que só dispara no segundo certo é um digest que não dispara.
    /// </para>
    /// <para>
    /// O primeiro digest do dia também cobre quem ligou o computador às 10h: o horário das 8h25
    /// já passou, nada rodou depois dele, então ele roda agora. Perder o dia inteiro por ter
    /// acordado tarde seria o contrário do que o vigia existe para fazer.
    /// </para>
    /// </summary>
    /// <param name="agora">Hora local.</param>
    /// <param name="ultimo">Quando o último digest rodou, em hora local. Nulo se nunca rodou.</param>
    public static bool HoraDoDigest(DateTime agora, DateTime? ultimo)
    {
        var marco = MarcoMaisRecente(agora);
        if (marco == null) return false;

        return ultimo == null || ultimo.Value < marco.Value;
    }

    /// <summary>
    /// O horário de digest mais recente que já passou HOJE, ou nulo antes do primeiro deles.
    /// <para>
    /// Antes das 8h25 não há digest atrasado a recuperar: o dia ainda não começou, e disparar
    /// nesse instante mostraria ao usuário a caixa de ontem como se fosse novidade.
    /// </para>
    /// </summary>
    public static DateTime? MarcoMaisRecente(DateTime agora)
    {
        var hoje = agora.Date;

        var passados = Horarios
            .Select(h => hoje + h)
            .Where(m => m <= agora)
            .ToList();

        return passados.Count == 0 ? null : passados.Max();
    }

    /// <summary>Se já passou o intervalo de sondagem desde a última.</summary>
    public static bool HoraDaSondagem(DateTime agora, DateTime? ultima) =>
        ultima == null || agora - ultima.Value >= IntervaloDaSondagem;
}

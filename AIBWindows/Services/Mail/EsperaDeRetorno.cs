using System;

namespace AIB.Services.Mail;

/// <summary>
/// Uma conversa em que a última palavra é sua: a linha da seção "Aguardando retorno".
/// <para>
/// Nasce da VIGIA (o prazo) cruzada com o HISTÓRICO da conversa (assunto, destinatário, quem
/// iniciou). Nenhum corpo: o que há aqui é cabeçalho e datas.
/// </para>
/// </summary>
/// <param name="Conta">A caixa. Vazia quando a conversa não tem histórico em disco.</param>
/// <param name="ThreadId">X-GM-THRID.</param>
/// <param name="Assunto">Vazio quando o histórico não guardou (diário desligado).</param>
/// <param name="Para">A quem você escreveu por último.</param>
/// <param name="EnviadaEm">Quando você escreveu por último, em UTC.</param>
/// <param name="Ate">Fim da vigia, em UTC.</param>
/// <param name="Mensagens">Quantas a conversa tem, contando as suas.</param>
/// <param name="Iniciada">Foi você quem começou.</param>
public sealed record EsperaDeRetorno(
    string Conta,
    string ThreadId,
    string Assunto,
    string Para,
    DateTime EnviadaEm,
    DateTime Ate,
    int Mensagens,
    bool Iniciada)
{
    /// <summary>Passou do prazo sem resposta.</summary>
    public bool Vencida(DateTime agoraUtc) => agoraUtc.Date > Ate.Date;

    /// <summary>Falta um dia ou menos: a hora de cobrar ou estender.</summary>
    public bool Vencendo(DateTime agoraUtc) => !Vencida(agoraUtc) && (Ate.Date - agoraUtc.Date).TotalDays <= 1;

    /// <summary>Dias inteiros até o fim do prazo; zero no último dia.</summary>
    public int DiasRestantes(DateTime agoraUtc) => Math.Max(0, (int)(Ate.Date - agoraUtc.Date).TotalDays);

    /// <summary>Quanto do prazo já passou, de 0 a 1 — a barra fina do item.</summary>
    public double Consumido(DateTime agoraUtc)
    {
        double total = (Ate - EnviadaEm).TotalSeconds;
        if (total <= 0) return 1;

        return Math.Clamp((agoraUtc - EnviadaEm).TotalSeconds / total, 0, 1);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace AIB.Services.Mail;

/// <summary>
/// Uma sequência de mensagens do mesmo remetente apertada o bastante para virar incidente.
/// </summary>
/// <param name="Email">Quem mandou.</param>
/// <param name="Nome">Nome legível do remetente.</param>
/// <param name="Quantas">Quantas mensagens entraram na janela.</param>
/// <param name="Comeco">A primeira delas.</param>
/// <param name="Fim">A última.</param>
/// <param name="Assuntos">Os assuntos, em ordem. O modelo lê isto para escrever o parágrafo.</param>
public sealed record Rajada(
    string Email, string Nome, int Quantas, DateTime Comeco, DateTime Fim, string[] Assuntos)
{
    public TimeSpan Duracao => Fim - Comeco;
}

/// <summary>
/// Encontra rajadas — o caso do firewall de 01/09.
/// <para>
/// O desenho original do funil teria jogado todas fora: remetente <c>noreply</c>, automático,
/// repetitivo, exatamente o que todo filtro barato mata. A premissa errada era tratar
/// "automático" como sinônimo de "ruído". <b>Mensagem automática sozinha é ruído; doze em
/// quarenta minutos é um incidente.</b>
/// </para>
/// <para>
/// O sinal não está em nenhuma mensagem — está no volume e no ritmo. Um funil que tria mensagem
/// a mensagem é estruturalmente cego para isso, por mais esperto que seja o modelo. Por isso a
/// contagem é de CÓDIGO: os números saem exatos porque não passaram por modelo nenhum. Ao 9B
/// cabe só escrever o parágrafo por cima de fatos já apurados.
/// </para>
/// </summary>
public static class DetectorDeRajada
{
    public static IReadOnlyList<Rajada> Encontrar(
        IEnumerable<MensagemDeEmail> mensagens, RegrasDoVigia regras)
    {
        var achadas = new List<Rajada>();
        if (mensagens == null || regras == null) return achadas;

        foreach (var grupo in mensagens.Where(m => m != null).GroupBy(m => m.De))
        {
            var fonte = regras.Fonte(grupo.Key);
            if (fonte == null) continue;

            var emOrdem = grupo.OrderBy(m => m.RecebidaUtc).ToList();
            var janela = MaiorJanela(emOrdem, fonte);
            if (janela == null) continue;

            achadas.Add(new Rajada(
                fonte.Email,
                emOrdem[0].NomeDoRemetente,
                janela.Count,
                janela[0].RecebidaUtc,
                janela[^1].RecebidaUtc,
                janela.Select(m => m.Assunto).ToArray()));
        }

        // Mais mensagens primeiro: a rajada maior é a que manda na tela.
        return achadas.OrderByDescending(r => r.Quantas).ToList();
    }

    /// <summary>
    /// A maior sequência que cabe na janela da regra, por duas pontas deslizantes.
    /// <para>
    /// Contar tudo o que veio do remetente no período inteiro da varredura misturaria três
    /// mensagens de manhã com três de tarde e chamaria isso de incidente. O que caracteriza a
    /// rajada é o APERTO — as mesmas seis espalhadas por seis horas não são nada.
    /// </para>
    /// </summary>
    private static List<MensagemDeEmail>? MaiorJanela(
        List<MensagemDeEmail> emOrdem, FonteDeAlerta fonte)
    {
        List<MensagemDeEmail>? melhor = null;
        int inicio = 0;

        for (int fim = 0; fim < emOrdem.Count; fim++)
        {
            while (emOrdem[fim].RecebidaUtc - emOrdem[inicio].RecebidaUtc > fonte.Janela)
                inicio++;

            int quantas = fim - inicio + 1;
            if (quantas >= fonte.Minimo && (melhor == null || quantas > melhor.Count))
                melhor = emOrdem.GetRange(inicio, quantas);
        }

        return melhor;
    }
}

using System;
using System.Collections.Generic;
using System.Windows.Media;
using AIB.Services.Memory;

using AIB.Services;

namespace AIB.Ui;

/// <summary>
/// Desenhos das ferramentas, num viewBox de 22x22 (§4.5 da spec de chat).
/// <para>
/// Aqui o ícone reflete a OPERAÇÃO — gravar, ler, executar, recusar. Na aba de arquivos do
/// painel lateral ele reflete o TIPO do arquivo, e lá a operação aparece na pill de origem. As
/// duas coisas são propositalmente diferentes: no meio de uma cadeia de ações o que se procura
/// é "o que ele fez", numa lista de arquivos é "o que é isto".
/// </para>
/// </summary>
public static class ToolIcons
{
    /// <summary>Traço padrão dos ícones desta família.</summary>
    public const double Espessura = 1.4;

    // Os desenhos são de traço, não de preenchimento: no tamanho de 22px uma silhueta cheia
    // vira mancha. Todos partem da mesma folha e da mesma espessura para a trilha ficar
    // homogênea.
    private const string Folha = "M6,3 H13 L17,7 V19 H6 Z M13,3 V7 H17";
    private const string Gravar = Folha + " M8.6,12.4 H14.4 M8.6,15.2 H12.6";
    private const string Ler = Folha + " M8.6,11 H14.4 M8.6,13.8 H14.4 M8.6,16.6 H12";
    private const string Terminal = "M4,4.5 H18 V17.5 H4 Z M7,8.6 L9.8,11 L7,13.4 M11.6,14 H15";
    private const string Negado = "M11,3.6 A7.4,7.4 0 1 1 11,18.4 A7.4,7.4 0 1 1 11,3.6 Z M6.2,6.2 L15.8,15.8";
    private const string Generico = "M11,4.6 A6.4,6.4 0 1 1 11,17.4 A6.4,6.4 0 1 1 11,4.6 Z M11,7.6 V11.4 M11,13.8 V14.2";

    private static readonly Dictionary<string, Geometry> Cache = new(StringComparer.Ordinal);

    /// <summary>Desenho para a natureza do artefato.</summary>
    public static Geometry De(ArtifactKind tipo) => Obter(tipo switch
    {
        ArtifactKind.FileWritten => Gravar,
        ArtifactKind.FileRead => Ler,
        ArtifactKind.CommandRun => Terminal,
        ArtifactKind.Denied => Negado,
        _ => Generico
    });

    /// <summary>
    /// Desenho pelo NOME da ferramenta, para quando ainda não há artefato — o chip em execução
    /// aparece antes de existir resultado.
    /// </summary>
    public static Geometry De(string? ferramenta) => Obter(ferramenta switch
    {
        Ferramentas.Gravar => Gravar,
        Ferramentas.Ler => Ler,
        Ferramentas.Shell => Terminal,
        Ferramentas.Habilidade => Terminal,
        _ => Generico
    });

    private static Geometry Obter(string dados)
    {
        // Geometry congelado e reaproveitado: a trilha pode ter dezenas de ícones numa conversa
        // longa, e refazer o parse a cada um é desperdício puro.
        lock (Cache)
        {
            if (Cache.TryGetValue(dados, out var pronto)) return pronto;

            var geo = Geometry.Parse(dados);
            geo.Freeze();
            Cache[dados] = geo;
            return geo;
        }
    }
}

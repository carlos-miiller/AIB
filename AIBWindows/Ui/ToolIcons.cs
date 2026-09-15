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

    // Lápis sobre a linha de base: o mesmo desenho do edit_file do mock (§6.4), no viewBox 22.
    private const string Editar = "M4.2,18.6 H17.8 M6.2,14.5 L14.5,6.2 A1.7,1.7 0 0 1 16.9,6.2 L17.3,6.6 A1.7,1.7 0 0 1 17.3,9 L9,17.3 H6.2 Z";
    private const string Procurar = "M10,4 A5.5,5.5 0 1 1 10,15 A5.5,5.5 0 1 1 10,4 Z M14,13.5 L18,17.5";
    // Lupa sobre linhas de texto: acha DENTRO do conteúdo, e não pelo nome.
    private const string Buscar = "M3.5,5 H9 M3.5,8.5 H7 M3.5,12 H7 M13,7.5 A3.8,3.8 0 1 1 13,15.1 A3.8,3.8 0 1 1 13,7.5 Z M15.8,14.2 L18.5,16.9";
    private const string Email = "M3.5,5.5 H18.5 V16.5 H3.5 Z M3.5,5.8 L11,11.6 L18.5,5.8";

    private static readonly Dictionary<string, Geometry> Cache = new(StringComparer.Ordinal);

    /// <summary>Desenho para a natureza do artefato.</summary>
    public static Geometry De(ArtifactKind tipo) => Obter(PeloTipo(tipo));

    /// <summary>
    /// Desenho pelo NOME da ferramenta, para quando ainda não há artefato — o chip em execução
    /// aparece antes de existir resultado.
    /// </summary>
    public static Geometry De(string? ferramenta) => Obter(PeloNome(ferramenta) ?? Generico);

    /// <summary>
    /// Desenho de uma ação concluída: a recusa vence tudo; depois o NOME, que distingue editar
    /// de gravar e procurar de ler; e o tipo do artefato só para ferramenta sem desenho próprio.
    /// </summary>
    public static Geometry De(string? ferramenta, ArtifactKind? tipo)
    {
        if (tipo == ArtifactKind.Denied) return Obter(Negado);

        string? dados = PeloNome(ferramenta);
        if (dados != null) return Obter(dados);

        return Obter(tipo is { } t ? PeloTipo(t) : Generico);
    }

    private static string PeloTipo(ArtifactKind tipo) => tipo switch
    {
        ArtifactKind.FileWritten => Gravar,
        ArtifactKind.FileRead => Ler,
        ArtifactKind.CommandRun => Terminal,
        ArtifactKind.Denied => Negado,
        _ => Generico
    };

    private static string? PeloNome(string? ferramenta) => ferramenta switch
    {
        Ferramentas.Gravar => Gravar,
        Ferramentas.Ler => Ler,
        Ferramentas.Editar => Editar,
        Ferramentas.Procurar => Procurar,
        Ferramentas.Buscar => Buscar,
        Ferramentas.Email or Ferramentas.LerEmail => Email,
        Ferramentas.Shell => Terminal,
        Ferramentas.Habilidade => Terminal,
        _ => null
    };

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

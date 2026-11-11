using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AIB.Services;

/// <summary>Um trecho da mensagem com cara de segredo.</summary>
/// <param name="Inicio">Posição no texto.</param>
/// <param name="Trecho">O valor, para o alerta guardar no cofre.</param>
/// <param name="Tipo">O que o alerta diz que parece: "um webhook com a chave no endereço".</param>
/// <param name="Nome">Nome sugerido para o alias ("bitrix"), ou null.</param>
/// <param name="Host">Host sugerido, quando o trecho é um endereço.</param>
public sealed record SegredoAchado(int Inicio, string Trecho, string Tipo, string? Nome, string? Host);

/// <summary>
/// O que a caixa de mensagem confere antes de enviar.
/// <para>
/// Pedido do usuário (20/10/2026): "vamos tentar detectar antes de enviar, e exibir um alerta na
/// tela". O segredo colado no chat vai ao provedor do modelo e fica no histórico; o filtro de
/// saída (<see cref="Segredos"/>) só olha o que as ferramentas devolvem, não o que o usuário
/// escreve.
/// </para>
/// <para>
/// Valor que já está no <see cref="Cofre"/> é trocado pelo nome sem perguntar. O resto é
/// palpite por formato: webhooks com a chave no caminho e os formatos que o filtro de saída
/// conhece. Senha solta, sem formato nem nome por perto, passa.
/// </para>
/// </summary>
public static class SegredoNaMensagem
{
    private const RegexOptions Opcoes = RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
    private static readonly TimeSpan Prazo = TimeSpan.FromSeconds(1);

    /// <summary>Webhooks que levam a chave no próprio endereço, com o nome que o alias ganha.</summary>
    private static readonly (Regex Padrao, string Nome)[] Webhooks =
    {
        (new(@"https?://[\w.-]*bitrix24\.[\w.]+/rest/\d+/[A-Za-z0-9]{8,}/?[^\s""'<>]*", Opcoes, Prazo), "bitrix"),
        (new(@"https?://[\w.-]+/rest/\d+/[A-Za-z0-9]{12,}/?[^\s""'<>]*", Opcoes, Prazo), "bitrix"),
        (new(@"https://hooks\.slack\.com/services/[A-Za-z0-9/_-]{20,}", Opcoes, Prazo), "slack"),
        (new(@"https://(?:\w+\.)?discord(?:app)?\.com/api/webhooks/\d+/[\w-]{20,}", Opcoes, Prazo), "discord"),
        (new(@"https://[\w.-]+\.webhook\.office\.com/[^\s""'<>]{20,}", Opcoes, Prazo), "teams"),
        (new(@"https?://[^\s""'<>]+[?&](?:token|key|apikey|api_key|secret|sig|access_token)=[A-Za-z0-9_\-.%]{16,}[^\s""'<>]*", Opcoes, Prazo), "webhook"),
    };

    /// <summary>
    /// O texto com os valores do cofre já trocados por <c>[cofre: nome]</c>, os nomes trocados, e
    /// o que mais parece segredo.
    /// </summary>
    public static (string Texto, IReadOnlyList<string> Trocados, IReadOnlyList<SegredoAchado> Achados) Conferir(string texto, Cofre cofre)
    {
        if (string.IsNullOrEmpty(texto)) return (texto ?? "", Array.Empty<string>(), Array.Empty<SegredoAchado>());

        string trocado = cofre.Redigir(texto, out int n);
        var nomes = n == 0
            ? new List<string>()
            : Regex.Matches(trocado, @"\[cofre: ([a-z0-9-]+)\]").Select(m => m.Groups[1].Value).Distinct().ToList();

        return (trocado, nomes, Achar(trocado));
    }

    /// <summary>Os trechos com cara de segredo, em ordem, sem sobreposição.</summary>
    public static IReadOnlyList<SegredoAchado> Achar(string texto)
    {
        var achados = new List<SegredoAchado>();
        if (string.IsNullOrEmpty(texto)) return achados;

        try
        {
            foreach (var (padrao, nome) in Webhooks)
                foreach (Match m in padrao.Matches(texto))
                    achados.Add(new SegredoAchado(m.Index, m.Value, "um webhook com a chave no endereço", nome, Cofre.HostDe(m.Value)));
        }
        catch (RegexMatchTimeoutException)
        {
            // Texto patológico: sem alerta por webhook. Os formatos abaixo ainda conferem.
        }

        foreach (var (inicio, tamanho) in Segredos.Trechos(texto))
        {
            string trecho = texto.Substring(inicio, tamanho);
            if (trecho.StartsWith("[cofre:", StringComparison.Ordinal)) continue;
            achados.Add(new SegredoAchado(inicio, trecho, "uma chave ou senha", null, null));
        }

        // Um webhook com ?token= também casa no filtro de saída: fica o maior, que é o webhook.
        var unicos = new List<SegredoAchado>();
        foreach (var a in achados.OrderBy(a => a.Inicio).ThenByDescending(a => a.Trecho.Length))
            if (unicos.Count == 0 || a.Inicio >= unicos[^1].Inicio + unicos[^1].Trecho.Length) unicos.Add(a);
        return unicos;
    }

    /// <summary>Troca o trecho achado pela marca do alias recém-guardado.</summary>
    public static string TrocarPeloNome(string texto, SegredoAchado achado, string nome) =>
        texto.Substring(0, achado.Inicio) + Cofre.Marca(nome) + texto.Substring(achado.Inicio + achado.Trecho.Length);
}

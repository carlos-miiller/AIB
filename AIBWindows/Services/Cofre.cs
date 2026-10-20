using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIB.Services;

/// <summary>
/// Um segredo do cofre, como a interface e o modelo o veem: tudo menos o valor.
/// </summary>
/// <param name="Nome">"bitrix". Vira a variável <c>COFRE_BITRIX</c>.</param>
/// <param name="Descricao">Para que serve. É o que o modelo lê para escolher o alias.</param>
/// <param name="Hosts">Os únicos endereços com que um comando que usa o alias pode falar.
/// Vazio: qualquer um, e o cartão avisa.</param>
/// <param name="Shell">"Permite uso no shell". Desligado, só uma habilidade o usa: o webhook do
/// Bitrix fica preso às operações que a habilidade dele permite, em vez da API inteira.</param>
public sealed record Alias(string Nome, string Descricao, IReadOnlyList<string> Hosts,
                           DateTime CriadoEm, DateTime? UsadoEm, int Usos, bool Shell = true)
{
    public string Variavel => Cofre.Variavel(Nome);
}

/// <summary>
/// Chaves, webhooks e senhas que a IA usa sem ler.
/// <para>
/// Caso real (19/10/2026): para mexer no Bitrix, a Ellen escrevia o webhook inteiro
/// (<c>https://….bitrix24.com.br/rest/1/&lt;chave&gt;/</c>) no comando. A chave ia ao provedor do
/// modelo, ao <c>raw.jsonl</c> e à auditoria, e o <see cref="Segredos"/> não a pegava: é uma URL
/// comum, sem nome de senha ao lado.
/// </para>
/// <para>
/// Aqui o usuário guarda o valor uma vez; a IA escreve <c>$env:COFRE_BITRIX</c> e o shell põe o
/// valor no processo só na hora de rodar (<see cref="Tools.RunCommandTool"/>). Na volta,
/// <see cref="Redigir"/> troca o valor por <c>[cofre: bitrix]</c> em qualquer saída de
/// ferramenta.
/// </para>
/// <para>
/// Mora em <c>~/.AIB/credentials/cofre</c>: as ferramentas de leitura já negam
/// <c>credentials/</c> (<see cref="DadosProtegidos"/>), e o reset de fábrica o leva junto. Não usa
/// o <see cref="CredentialService"/> pelo motivo do <see cref="Mail.MailVault"/>: ele procura a
/// chave nos outros arquivos quando não acha a pedida.
/// </para>
/// </summary>
public sealed class Cofre
{
    /// <summary>Sobrescreve <c>~/.AIB</c>. Os testes desviam no <c>TestAuditRedirect</c>.</summary>
    public static string? RaizPadrao { get; set; }

    /// <summary>Prefixo da variável. Também é o que o shell procura no comando.</summary>
    public const string Prefixo = "COFRE_";

    private static readonly object Trava = new();
    private static readonly Regex NomeValido = new(@"^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.CultureInvariant);

    private readonly string _arquivo;

    public Cofre(string? raizDeDados = null)
    {
        string raiz = raizDeDados ?? RaizPadrao ?? DirectoryService.DataDir;
        _arquivo = Path.Combine(raiz, "credentials", "cofre", "aliases.bin");
    }

    /// <summary>"sankhya-leitura" → "COFRE_SANKHYA_LEITURA".</summary>
    public static string Variavel(string nome) =>
        Prefixo + nome.Trim().ToUpperInvariant().Replace('-', '_');

    /// <summary>O que fica no lugar do valor, na saída e na mensagem do usuário.</summary>
    public static string Marca(string nome) => $"[cofre: {nome}]";

    /// <summary>Os aliases, sem os valores, por nome.</summary>
    public IReadOnlyList<Alias> Listar() =>
        Ler().Select(i => i.ParaAlias()).OrderBy(a => a.Nome, StringComparer.Ordinal).ToList();

    public Alias? Achar(string nome) => Listar().FirstOrDefault(a => a.Nome == Normalizar(nome));

    /// <summary>O alias cuja variável é esta (<c>COFRE_BITRIX</c>), sem olhar maiúsculas.</summary>
    public Alias? PelaVariavel(string variavel) =>
        Listar().FirstOrDefault(a => string.Equals(a.Variavel, variavel, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Guarda (ou substitui) um alias. Lança <see cref="ArgumentException"/> com a frase que a
    /// tela mostra.
    /// </summary>
    /// <param name="valor">Vazio num alias que já existe: mantém o valor guardado (a tela edita
    /// descrição, hosts e a flag sem pedir o segredo de novo).</param>
    /// <param name="shell">"Permite uso no shell".</param>
    public void Guardar(string nome, string? valor, string descricao, IEnumerable<string> hosts, bool shell = true)
    {
        nome = Normalizar(nome);
        if (!NomeValido.IsMatch(nome))
            throw new ArgumentException("O nome usa só letras minúsculas, números e hífen (até 40), começando por letra ou número.");
        if (string.IsNullOrWhiteSpace(valor))
        {
            valor = Ler().FirstOrDefault(i => i.Nome == nome)?.Valor;
            if (valor == null) throw new ArgumentException("O valor está vazio.");
        }
        if (string.IsNullOrWhiteSpace(descricao))
            throw new ArgumentException("Diga para que serve: é o que a IA lê para escolher o alias.");

        var lista = hosts.Select(NormalizarHost).Where(h => h.Length > 0).Distinct().ToList();

        // Um webhook é ele mesmo um endereço: se o host dele não está na lista, todo comando que
        // o usasse seria recusado pelo próprio cofre.
        string? doValor = HostDe(valor.Trim());
        if (doValor != null && lista.Count > 0 && !lista.Contains(doValor))
            throw new ArgumentException($"O valor é um endereço de {doValor}, que não está nos hosts permitidos.");

        lock (Trava)
        {
            var itens = Ler();
            var antigo = itens.FirstOrDefault(i => i.Nome == nome);
            itens.RemoveAll(i => i.Nome == nome);
            itens.Add(new Item
            {
                Nome = nome,
                Valor = valor.Trim(),
                Descricao = descricao.Trim(),
                Hosts = lista,
                Shell = shell,
                CriadoEm = antigo?.CriadoEm ?? DateTime.Now,
                UsadoEm = antigo?.UsadoEm,
                Usos = antigo?.Usos ?? 0
            });
            Gravar(itens);
        }
    }

    /// <summary>Troca só o valor, mantendo descrição, hosts e contagem.</summary>
    public void TrocarValor(string nome, string valor)
    {
        var a = Achar(nome) ?? throw new ArgumentException($"Não há '{nome}' no cofre.");
        Guardar(a.Nome, valor, a.Descricao, a.Hosts, a.Shell);
    }

    public void Apagar(string nome)
    {
        nome = Normalizar(nome);
        lock (Trava)
        {
            var itens = Ler();
            if (itens.RemoveAll(i => i.Nome == nome) > 0) Gravar(itens);
        }
    }

    /// <summary>Conta um uso, para a tela dizer "usado 3× · ontem 17:40".</summary>
    public void RegistrarUso(IEnumerable<string> nomes)
    {
        var usados = nomes.Select(Normalizar).ToHashSet();
        if (usados.Count == 0) return;

        lock (Trava)
        {
            var itens = Ler();
            foreach (var i in itens.Where(i => usados.Contains(i.Nome)))
            {
                i.Usos++;
                i.UsadoEm = DateTime.Now;
            }
            Gravar(itens);
        }
    }

    /// <summary>
    /// As variáveis de ambiente destes aliases, com os valores. Só o shell chama, na hora de
    /// rodar um comando já autorizado.
    /// </summary>
    internal IReadOnlyDictionary<string, string> Variaveis(IEnumerable<string> nomes)
    {
        var pedidos = nomes.Select(Normalizar).ToHashSet();
        return Ler().Where(i => pedidos.Contains(i.Nome)).ToDictionary(i => Variavel(i.Nome), i => i.Valor);
    }

    /// <summary>Teto abaixo do qual o valor não é trocado: "abc" apareceria em todo lugar.</summary>
    private const int Minimo = 6;

    /// <summary>
    /// O texto com cada valor do cofre trocado por <c>[cofre: nome]</c>, também nas formas em que
    /// ele volta de uma API ou de um eco: codificado para URL e em base64.
    /// <para>
    /// É troca EXATA, ao contrário do <see cref="Segredos"/>, que adivinha pelo formato. Por isso
    /// pega o webhook, que não tem formato de segredo nenhum.
    /// </para>
    /// </summary>
    public string Redigir(string? texto) => Redigir(texto, out _);

    public string Redigir(string? texto, out int trocas)
    {
        trocas = 0;
        if (string.IsNullOrEmpty(texto)) return texto ?? "";

        List<Item> itens;
        try { itens = Ler(); }
        catch { return texto; }

        // Do mais longo para o mais curto: um valor que contém outro sai inteiro.
        foreach (var (forma, nome) in itens
                     .Where(i => i.Valor.Length >= Minimo)
                     .SelectMany(i => Formas(i.Valor).Select(f => (f, i.Nome)))
                     .OrderByDescending(p => p.f.Length))
        {
            int n = Contar(texto, forma);
            if (n == 0) continue;
            trocas += n;
            texto = texto.Replace(forma, Marca(nome), StringComparison.Ordinal);
        }
        return texto;
    }

    private static int Contar(string texto, string parte)
    {
        int n = 0, i = 0;
        while ((i = texto.IndexOf(parte, i, StringComparison.Ordinal)) >= 0) { n++; i += parte.Length; }
        return n;
    }

    private static IEnumerable<string> Formas(string valor)
    {
        var formas = new HashSet<string>(StringComparer.Ordinal) { valor };

        // Webhook com ou sem a barra final: a API devolve de um jeito, o usuário cola do outro.
        if (valor.EndsWith('/') && valor.Length > Minimo) formas.Add(valor.TrimEnd('/'));

        formas.Add(Uri.EscapeDataString(valor));
        formas.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(valor)));
        return formas.Where(f => f.Length >= Minimo);
    }

    /// <summary>"https://cpaps.bitrix24.com.br/rest/1/x/" → "cpaps.bitrix24.com.br". Null se não é URL.</summary>
    public static string? HostDe(string texto) =>
        Uri.TryCreate(texto, UriKind.Absolute, out var u) && !u.IsFile && u.Host.Length > 0
            ? u.Host.ToLowerInvariant()
            : null;

    /// <summary>Aceita host puro ou URL colada inteira; devolve só o host, em minúsculas.</summary>
    public static string NormalizarHost(string? host)
    {
        string h = (host ?? "").Trim();
        if (h.Length == 0) return "";
        return HostDe(h) ?? h.Split('/', ':')[0].ToLowerInvariant();
    }

    private static string Normalizar(string nome) => (nome ?? "").Trim().ToLowerInvariant();

    // ── disco ───────────────────────────────────────────────────────────────

    private sealed class Item
    {
        public string Nome { get; set; } = "";
        public string Valor { get; set; } = "";
        public string Descricao { get; set; } = "";
        public List<string> Hosts { get; set; } = new();

        /// <summary>Verdadeiro quando o arquivo é de antes da flag: era o comportamento de então.</summary>
        public bool Shell { get; set; } = true;
        public DateTime CriadoEm { get; set; }
        public DateTime? UsadoEm { get; set; }
        public int Usos { get; set; }

        public Alias ParaAlias() => new(Nome, Descricao, Hosts, CriadoEm, UsadoEm, Usos, Shell);
    }

    /// <summary>
    /// Os itens, ou lista vazia. Arquivo que não decifra (de outra máquina, de outro usuário)
    /// também é vazio: não há o que fazer com ele, e lançar derrubaria toda saída de ferramenta.
    /// </summary>
    private List<Item> Ler()
    {
        try
        {
            if (!File.Exists(_arquivo)) return new();
            byte[] claro = ProtectedData.Unprotect(File.ReadAllBytes(_arquivo), null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<List<Item>>(claro) ?? new();
        }
        catch
        {
            return new();
        }
    }

    /// <summary>Por um temporário: uma queda no meio deixa o cofre anterior, e não meio cofre.</summary>
    private void Gravar(List<Item> itens)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_arquivo)!);
        byte[] claro = JsonSerializer.SerializeToUtf8Bytes(itens);
        byte[] cifrado = ProtectedData.Protect(claro, null, DataProtectionScope.CurrentUser);
        Array.Clear(claro);

        string tmp = _arquivo + ".tmp";
        File.WriteAllBytes(tmp, cifrado);
        File.Move(tmp, _arquivo, overwrite: true);
    }
}

using System;
using System.Globalization;
using System.IO;

namespace AIB.Services;

/// <summary>
/// A limpeza do reset de fábrica: a memória das conversas e os arquivos de e-mail.
/// <para>
/// Vive fora da tela porque é a parte que tem REGRA. O diálogo antigo mostrava a pasta
/// <c>memory</c> como alvo e não encostava nela: prometia o que não fazia. Agora apaga de
/// verdade — e uma promessa cumprida sobre arquivo apagado precisa de ensaio, o que uma
/// rotina dentro do <c>Click</c> não permite.
/// </para>
/// <para>
/// DUAS EXCEÇÕES, e as duas são renomeação, não exclusão:
/// <list type="bullet">
/// <item><description><c>raw.jsonl</c> — regra do projeto: ele nunca é apagado. É a transcrição
/// crua da conversa, e resumo é perda irreversível.</description></item>
/// <item><description><c>facts.md</c> — é do USUÁRIO. Ele abre, edita e reordena à mão; o
/// programa só acrescenta linha no fim. Apagar o que a pessoa escreveu não é "voltar ao estado
/// de fábrica", é perder o trabalho dela.</description></item>
/// </list>
/// Os dois viram <c>&lt;nome&gt;.&lt;carimbo&gt;.&lt;ext&gt;.bak</c>. O <c>.bak</c> tira o
/// arquivo do caminho de quem lê (a sessão nova não o enxerga) sem tirá-lo do disco.
/// </para>
/// <para>
/// Nada fora de <c>memory/</c> e <c>email/</c> é tocado. Credenciais, configurações e o
/// <c>chat_history.json</c> continuam com quem já cuidava deles; os <c>logs/</c> ficam, porque
/// auditoria que o reset apaga não é auditoria.
/// </para>
/// </summary>
public static class ResetDeFabrica
{
    /// <summary>
    /// O carimbo do <c>.bak</c>. Minuto basta: dois resets no mesmo minuto são o mesmo gesto
    /// repetido, e para esse caso há o desempate por número.
    /// </summary>
    public const string FormatoDoCarimbo = "yyyyMMdd-HHmm";

    /// <summary>As pastas varridas, relativas à raiz de dados.</summary>
    private static readonly string[] Pastas = { "memory", "email" };

    /// <summary>
    /// Os arquivos que são renomeados em vez de apagados. Comparação por NOME, e não por
    /// caminho: <c>raw.jsonl</c> existe uma vez por sessão, e a lista de sessões é aberta.
    /// </summary>
    private static readonly string[] NomesPreservados = { "raw.jsonl", "facts.md" };

    /// <summary>O que a limpeza fez. Existe para a auditoria e para os ensaios.</summary>
    /// <param name="Apagados">Arquivos removidos.</param>
    /// <param name="Preservados">Arquivos renomeados para <c>.bak</c>.</param>
    /// <param name="Falhas">Arquivos que o sistema não deixou mexer.</param>
    public sealed record Resultado(int Apagados, int Preservados, int Falhas);

    /// <summary>
    /// Varre <c>memory/</c> e <c>email/</c> da raiz de dados.
    /// <para>
    /// NUNCA lança. Arquivo travado por outro processo é um arquivo que fica, não o reset
    /// inteiro que morre pela metade — e a metade que morre é sempre a que ainda não rodou.
    /// A falha vai para o console, que é onde se diagnostica.
    /// </para>
    /// </summary>
    /// <param name="raizDeDados">Raiz a limpar. Vazio significa a do usuário — os ensaios passam uma temporária.</param>
    /// <param name="quando">O relógio do carimbo. Existe para o ensaio não depender da hora da máquina.</param>
    public static Resultado Limpar(string? raizDeDados = null, DateTime? quando = null)
    {
        string raiz = string.IsNullOrWhiteSpace(raizDeDados)
            ? DirectoryService.DataDir
            : raizDeDados!;

        string carimbo = (quando ?? DateTime.Now).ToString(FormatoDoCarimbo, CultureInfo.InvariantCulture);

        var conta = new Contagem();

        foreach (string pasta in Pastas)
            Varrer(Path.Combine(raiz, pasta), carimbo, conta);

        Console.WriteLine($"[RESET] {conta.Apagados} arquivo(s) apagado(s), "
                          + $"{conta.Preservados} preservado(s) como .bak, {conta.Falhas} falha(s).");

        return new Resultado(conta.Apagados, conta.Preservados, conta.Falhas);
    }

    /// <summary>
    /// O nome do <c>.bak</c>: o carimbo entra ANTES da extensão original, para
    /// <c>raw.jsonl</c> virar <c>raw.20260922-1430.jsonl.bak</c> e continuar se lendo como o
    /// que é. Público porque é a promessa que o diálogo faz e que o ensaio confere.
    /// </summary>
    public static string NomeDoBackup(string nomeDoArquivo, string carimbo)
    {
        string sem = Path.GetFileNameWithoutExtension(nomeDoArquivo);
        string ext = Path.GetExtension(nomeDoArquivo);

        return $"{sem}.{carimbo}{ext}.bak";
    }

    /// <summary>
    /// Se este arquivo é preservado. O <c>.bak</c> entra na lista porque um segundo reset
    /// apagaria o cru salvo pelo primeiro — e aí a regra teria durado um reset.
    /// </summary>
    private static bool Preservar(string nome) =>
        nome.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
        || Array.Exists(NomesPreservados, p => string.Equals(p, nome, StringComparison.OrdinalIgnoreCase));

    private static void Varrer(string pasta, string carimbo, Contagem conta)
    {
        string[] arquivos;
        string[] subpastas;

        try
        {
            if (!Directory.Exists(pasta)) return;

            arquivos = Directory.GetFiles(pasta);
            subpastas = Directory.GetDirectories(pasta);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RESET] não consegui ler {pasta}: {ex.Message}");
            conta.Falhas++;
            return;
        }

        foreach (string arquivo in arquivos)
        {
            string nome = Path.GetFileName(arquivo);

            try
            {
                if (Preservar(nome))
                {
                    if (!nome.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                        File.Move(arquivo, Livre(pasta, nome, carimbo));

                    conta.Preservados++;
                    continue;
                }

                File.Delete(arquivo);
                conta.Apagados++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RESET] não consegui limpar {arquivo}: {ex.Message}");
                conta.Falhas++;
            }
        }

        foreach (string subpasta in subpastas)
            Varrer(subpasta, carimbo, conta);

        // Pasta que ficou vazia sai junto: uma sessão sem nada dentro é ruído, e é assim que
        // email/ desaparece inteira. A que guardou um .bak fica, com ele dentro.
        try
        {
            if (Directory.GetFileSystemEntries(pasta).Length == 0) Directory.Delete(pasta);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RESET] não consegui remover {pasta}: {ex.Message}");
        }
    }

    /// <summary>
    /// Um caminho que ainda não existe. Dois resets no MESMO minuto cairiam no mesmo nome, e um
    /// <c>File.Move</c> por cima do primeiro apagaria justamente o cru que ele salvou.
    /// <para>
    /// O desempate entra no CARIMBO, e não depois do <c>.bak</c>: o nome continua se lendo da
    /// esquerda para a direita como "raw, de tal instante, jsonl, cópia".
    /// </para>
    /// </summary>
    private static string Livre(string pasta, string nome, string carimbo)
    {
        for (int i = 1; i < 1000; i++)
        {
            string caminho = Path.Combine(
                pasta, NomeDoBackup(nome, i == 1 ? carimbo : $"{carimbo}-{i}"));

            if (!File.Exists(caminho)) return caminho;
        }

        // Mil cópias no mesmo minuto é impossível na prática; devolver o nome base faz o
        // File.Move estourar, e o estouro vira falha contada em vez de silêncio.
        return Path.Combine(pasta, NomeDoBackup(nome, carimbo));
    }

    private sealed class Contagem
    {
        public int Apagados;
        public int Preservados;
        public int Falhas;
    }
}

using System;
using System.IO;

namespace AIB.Services;

/// <summary>
/// O reset de fábrica: apaga TUDO o que o AIB guardou na raiz de dados (<c>~/.AIB</c>).
/// <para>
/// Decisão do usuário: reset é total. Preferências, cofres, histórico, memória, e-mail, skills,
/// personagens, perfil do navegador e logs — nada fica, nem cópia <c>.bak</c>. A versão anterior
/// varria só <c>memory/</c> e <c>email/</c> e renomeava <c>raw.jsonl</c> e <c>facts.md</c>: quem
/// resetava para entregar a máquina deixava para trás a conversa crua, os logins do navegador
/// e a auditoria.
/// </para>
/// <para>
/// É a ÚNICA exceção à regra de que o <c>raw.jsonl</c> nunca é apagado: nenhum caminho do uso
/// normal o remove; este só roda por um botão, depois de uma confirmação que diz que nada fica.
/// </para>
/// <para>
/// Vive fora da tela porque apagar arquivo precisa de ensaio com raiz temporária, o que uma
/// rotina dentro do <c>Click</c> não permite.
/// </para>
/// </summary>
public static class ResetDeFabrica
{
    /// <summary>O que a limpeza fez. Existe para a mensagem final e para os ensaios.</summary>
    /// <param name="Apagados">Arquivos removidos.</param>
    /// <param name="Falhas">Arquivos ou pastas que o sistema não deixou remover.</param>
    public sealed record Resultado(int Apagados, int Falhas);

    /// <summary>
    /// Apaga tudo o que há DENTRO da raiz de dados.
    /// <para>
    /// A pasta raiz fica, vazia: se ela sumisse, o próximo arranque veria a pasta antiga em
    /// <c>%AppData%\AIB</c> (de antes da mudança para <c>~/.AIB</c>) e a migraria de volta —
    /// os dados apagados reapareceriam.
    /// </para>
    /// <para>
    /// NUNCA lança. Arquivo travado por outro processo é um arquivo que fica, não o reset
    /// inteiro que morre pela metade — e a metade que morre é sempre a que ainda não rodou.
    /// A falha vai para o console e para a contagem.
    /// </para>
    /// </summary>
    /// <param name="raizDeDados">Raiz a limpar. Vazio significa a do usuário — os ensaios passam uma temporária.</param>
    public static Resultado Limpar(string? raizDeDados = null)
    {
        string raiz = string.IsNullOrWhiteSpace(raizDeDados)
            ? DirectoryService.DataDir
            : raizDeDados!;

        var conta = new Contagem();

        if (!RaizSegura(raiz))
        {
            Console.WriteLine($"[RESET] recusado: {raiz} não é uma pasta só do AIB.");
            return new Resultado(0, 1);
        }

        if (Directory.Exists(raiz)) Esvaziar(raiz, conta);

        Console.WriteLine($"[RESET] {conta.Apagados} arquivo(s) apagado(s), {conta.Falhas} falha(s).");

        return new Resultado(conta.Apagados, conta.Falhas);
    }

    /// <summary>
    /// Se a raiz pode ser esvaziada. A raiz de dados é configurável (<c>DataDirectory</c>), e
    /// "apagar tudo" numa pasta que não é só do AIB apagaria o que não é dele: recusa a raiz de
    /// uma unidade e qualquer pasta que CONTENHA o perfil do usuário ou o próprio programa.
    /// </summary>
    public static bool RaizSegura(string raiz)
    {
        try
        {
            string alvo = Normal(raiz);
            if (alvo.Length == 0) return false;
            if (string.Equals(alvo, Normal(Path.GetPathRoot(alvo) ?? ""), StringComparison.OrdinalIgnoreCase))
                return false;

            return !Contem(alvo, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
                && !Contem(alvo, AppContext.BaseDirectory);
        }
        catch
        {
            return false;
        }
    }

    private static string Normal(string caminho) =>
        caminho.Length == 0
            ? ""
            : Path.GetFullPath(caminho).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>Se <paramref name="dentro"/> é a própria <paramref name="pasta"/> ou está abaixo dela.</summary>
    private static bool Contem(string pasta, string dentro)
    {
        string d = Normal(dentro);
        return d.Length > 0
            && (string.Equals(d, pasta, StringComparison.OrdinalIgnoreCase)
                || d.StartsWith(pasta + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private static void Esvaziar(string pasta, Contagem conta)
    {
        string[] arquivos;
        string[] subpastas;

        try
        {
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
            try
            {
                // O perfil do Edge e pastas de skill vindas de um repositório trazem arquivos
                // só-leitura, e o File.Delete recusa esses.
                File.SetAttributes(arquivo, FileAttributes.Normal);
                File.Delete(arquivo);
                conta.Apagados++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RESET] não consegui apagar {arquivo}: {ex.Message}");
                conta.Falhas++;
            }
        }

        foreach (string subpasta in subpastas)
        {
            try
            {
                // Atalho de pasta (junção ou link) sai sozinho: descer por ele apagaria o
                // destino, que fica fora da raiz e não é do AIB.
                bool atalho = (File.GetAttributes(subpasta) & FileAttributes.ReparsePoint) != 0;
                if (!atalho) Esvaziar(subpasta, conta);

                Directory.Delete(subpasta);
            }
            catch (Exception ex)
            {
                // Pasta que guardou um arquivo travado cai aqui; a falha do arquivo já foi contada.
                Console.WriteLine($"[RESET] não consegui remover {subpasta}: {ex.Message}");
            }
        }
    }

    private sealed class Contagem
    {
        public int Apagados;
        public int Falhas;
    }
}

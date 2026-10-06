using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AIB.Services.Navegador;

/// <summary>
/// Os sites que o usuário já autorizou a IA a abrir. O primeiro acesso a um domínio pede cartão;
/// aprovado, o domínio entra aqui e abrir e ler nele deixam de perguntar.
/// <para>
/// POR QUE O PRIMEIRO ACESSO PERGUNTA. Uma página pode mandar a IA abrir
/// <c>site-malicioso.com/?dados=...</c> — o vazamento é o próprio endereço. Domínio que o usuário
/// nunca viu passar não é aberto sem ele ver.
/// </para>
/// <para>
/// Um arquivo de texto, um domínio por linha, em <c>~/.AIB/navegador/sites-liberados.txt</c>:
/// dá para ler e apagar à mão. O caminho é injetável para os ensaios não tocarem o ~/.AIB real.
/// </para>
/// </summary>
public sealed class SitesLiberados
{
    private readonly string _arquivo;
    private readonly object _gate = new();

    public SitesLiberados(string arquivo) => _arquivo = arquivo;

    public static string ArquivoPadrao => Path.Combine(DirectoryService.DataDir, "navegador", "sites-liberados.txt");

    public bool Contem(string dominio)
    {
        string d = Normalizar(dominio);
        return d.Length > 0 && Ler().Contains(d);
    }

    public void Adicionar(string dominio)
    {
        string d = Normalizar(dominio);
        if (d.Length == 0) return;

        lock (_gate)
        {
            var atuais = Ler();
            if (!atuais.Add(d)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_arquivo)!);
            File.WriteAllLines(_arquivo, atuais.OrderBy(x => x, StringComparer.Ordinal));
        }
    }

    public IReadOnlyCollection<string> Listar() => Ler();

    private HashSet<string> Ler()
    {
        lock (_gate)
        {
            try
            {
                return File.Exists(_arquivo)
                    ? File.ReadAllLines(_arquivo).Select(Normalizar).Where(l => l.Length > 0).ToHashSet(StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);
            }
            catch (IOException)
            {
                // Arquivo ilegível é lista vazia: o pior caso é perguntar de novo, nunca liberar.
                return new HashSet<string>(StringComparer.Ordinal);
            }
        }
    }

    private static string Normalizar(string? dominio) => (dominio ?? "").Trim().TrimEnd('.').ToLowerInvariant();
}

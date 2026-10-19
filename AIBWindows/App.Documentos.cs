using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using AIB.Services;
using AIB.Services.Documentos;
using AIB.Services.Tools;

namespace AIB;

/// <summary>
/// A interface da ferramenta <c>doc</c> que mora no App: a janela de escolha do arquivo travado,
/// o aviso "Gravado" com o Desfazer, e a limpeza das cópias velhas.
/// </summary>
public partial class App
{
    /// <summary>As cópias dos documentos, em ~/.AIB/doc-backups. A mesma pasta que a ferramenta usa.</summary>
    private readonly CopiasDeDocumento _copias = new();

    private void LigarDocumentos()
    {
        // Pedido do usuário (19/10/2026): cópias com mais de 3 meses saem, para as versões
        // guardadas não lotarem o disco. Em segundo plano: o arranque não espera disco.
        _ = Task.Run(() =>
        {
            int n = _copias.Limpar();
            if (n > 0) Console.WriteLine($"[DOC] {n} cópia(s) com mais de 3 meses apagada(s).");
        });

        DocTool.AoTravar = caminho => Dispatcher.InvokeAsync(async () =>
        {
            int escolha = await Views.AvisoWindow.EscolherAsync(Views.Avisos.DeTrava(caminho));
            return escolha switch
            {
                0 => DocTool.NaTrava.TentarDeNovo,
                1 => DocTool.NaTrava.SalvarCopia,
                _ => DocTool.NaTrava.Desistir
            };
        }).Task.Unwrap();

        DocTool.Gravou += g => Dispatcher.BeginInvoke(() => AvisarGravado(g));
    }

    private void AvisarGravado(DocTool.DocumentoGravado g)
    {
        var (receita, vez) = Views.Avisos.DeGravado(g.Arquivo, g.Criado, g.Copia != null,
            abrir: () => AbrirNoWindows(g.Arquivo),
            abrirPasta: () => AbrirNoWindows("explorer.exe", $"/select,\"{g.Arquivo}\""),
            desfazer: () => Desfazer(g));

        Views.Flutuantes.Mostrar(Views.AvisoWindow.Montar(receita), vez);
    }

    /// <summary>
    /// Põe a cópia de volta. Recusa se o arquivo mudou depois da gravação — restaurar apagaria o
    /// que o usuário fez depois. O resultado vem num aviso, e não num modal: ninguém está
    /// esperando a resposta para continuar.
    /// </summary>
    private void Desfazer(DocTool.DocumentoGravado g)
    {
        Views.AvisoWindow.Receita receita;
        try
        {
            string msg = _copias.Restaurar(g.Arquivo, g.Copia!, g.Impressao);
            receita = new Views.AvisoWindow.Receita(Views.AvisoWindow.Tom.Verde, "Desfeito", Views.AvisoWindow.IconeOk,
                Titulo: msg, FechaSozinho: Views.Avisos.PrazoDaResposta);
        }
        catch (Exception ex)
        {
            receita = new Views.AvisoWindow.Receita(Views.AvisoWindow.Tom.Perigo, "Não deu para desfazer", Views.AvisoWindow.IconeAlerta,
                Titulo: Path.GetFileName(g.Arquivo), Sub: ex.Message,
                Botoes: new[] { new Views.AvisoWindow.Botao("Entendi", () => { }, Principal: true) });
        }

        Views.Flutuantes.Mostrar(Views.AvisoWindow.Montar(receita), VezDaJanela.Pronta);
    }

    private static void AbrirNoWindows(string alvo, string? argumentos = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(alvo) { UseShellExecute = true, Arguments = argumentos ?? "" });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DOC] não consegui abrir '{alvo}': {ex.Message}");
        }
    }
}

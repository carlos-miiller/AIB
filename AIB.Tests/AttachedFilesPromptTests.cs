using System;
using System.IO;
using System.Linq;
using AIB.Services;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O bloco que apresenta ao modelo os arquivos anexados pelo usuário.
    /// <para>
    /// Antes disto, anexar pelo "+" do painel não informava nada ao modelo: a lista era
    /// contabilidade da interface. Pedir "analisa o relatório" pelo nome levava o modelo a
    /// chutar um caminho, ou a varrer o disco com shell.
    /// </para>
    /// <para>
    /// A lista do <see cref="ContextService"/> é estática e a suíte roda classes em paralelo,
    /// por isso cada ensaio limpa o que sujou. Sem isso um ensaio veria os anexos do outro.
    /// </para>
    /// </summary>
    [Collection("ContextoGlobal")]
    public class AttachedFilesPromptTests : IDisposable
    {
        public void Dispose()
        {
            foreach (var arquivo in ContextService.ActiveFiles.ToList())
                ContextService.RemoveFile(arquivo);
        }

        [Fact]
        public void SemAnexos_NaoHaBloco()
        {
            // Bloco vazio quer dizer que nada é inserido no prompt: um cabeçalho anunciando
            // uma lista vazia gastaria tokens para dizer que não há nada a dizer.
            ContextService.RenderizarAnexados().Should().BeEmpty();
        }

        [Fact]
        public void ArquivoAnexado_EntraComOCaminhoLiteral()
        {
            string caminho = Path.Combine(Path.GetTempPath(), "relatorio-" + Guid.NewGuid().ToString("N") + ".xlsx");

            ContextService.AddFile(caminho, ContextOrigin.AttachedByUser);

            string bloco = ContextService.RenderizarAnexados();

            bloco.Should().Contain(caminho, "o modelo precisa do caminho exato para chamar read");
            bloco.Should().Contain("read");
        }

        [Fact]
        public void OQueAIaLeu_NaoEntraNoBloco()
        {
            // Já está no histórico da conversa. Repetir aqui é pagar duas vezes pela mesma
            // informação, e a lista aceita até 30 itens.
            string lido = Path.Combine(Path.GetTempPath(), "lido-" + Guid.NewGuid().ToString("N") + ".cs");
            string anexado = Path.Combine(Path.GetTempPath(), "anexado-" + Guid.NewGuid().ToString("N") + ".cs");

            ContextService.AddFile(lido, ContextOrigin.ReadByAi);
            ContextService.AddFile(anexado, ContextOrigin.AttachedByUser);

            string bloco = ContextService.RenderizarAnexados();

            bloco.Should().Contain(anexado);
            bloco.Should().NotContain(lido);
        }

        [Fact]
        public void OConteudoDoArquivo_NuncaEntra()
        {
            // Só o caminho. Uma planilha de 240 KB não cabe na janela, e a decisão de ler é do
            // modelo — ele lê o que precisar, quando precisar.
            string caminho = Path.Combine(Path.GetTempPath(), "segredo-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(caminho, "CONTEUDO_QUE_NAO_PODE_VAZAR");

            try
            {
                ContextService.AddFile(caminho, ContextOrigin.AttachedByUser);

                ContextService.RenderizarAnexados()
                    .Should().NotContain("CONTEUDO_QUE_NAO_PODE_VAZAR");
            }
            finally
            {
                File.Delete(caminho);
            }
        }
    }
}

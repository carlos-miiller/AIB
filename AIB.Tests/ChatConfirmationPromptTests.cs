using System;
using System.Threading.Tasks;
using AIB.Services;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// O portão que pergunta dentro da conversa. Este ensaio cobre o que ele NÃO pode fazer.
    /// <para>
    /// A regra é uma só: nada executa sem alguém autorizar. Toda saída que não seja um clique
    /// explícito em "Permitir" — falta de interface, exceção ao montar o card, card descartado
    /// — tem de virar recusa. Um portão que falha para o lado permissivo é pior que portão
    /// nenhum, porque dá a impressão de que existe proteção.
    /// </para>
    /// </summary>
    public class ChatConfirmationPromptTests
    {
        private static CommandConfirmationContext Contexto() => new()
        {
            Command = "Remove-Item -Recurse C:\\temp",
            Tool = "shell",
            Level = 5,
            Cwd = "C:\\"
        };

        [Fact]
        public async Task SemInterfaceConectada_Recusa()
        {
            // É o estado do app entre a construção do registry e o nascimento da janela.
            var portao = new ChatConfirmationPrompt();

            var (permitido, sempre) = await portao.AskAsync(Contexto());

            permitido.Should().BeFalse("sem alguém para autorizar, nada executa");
            sempre.Should().BeFalse();
        }

        [Fact]
        public async Task InterfaceDesconectada_VoltaARecusar()
        {
            // A janela morreu: o portão precisa voltar ao padrão seguro, e não seguir chamando
            // um apresentador que já não existe.
            var portao = new ChatConfirmationPrompt();
            portao.Conectar(_ => Task.FromResult((true, false)));
            portao.Conectar(null);

            (await portao.AskAsync(Contexto())).Allowed.Should().BeFalse();
        }

        [Fact]
        public async Task ApresentadorQueExplode_Recusa()
        {
            // Falha ao MOSTRAR a pergunta não pode virar autorização silenciosa.
            var portao = new ChatConfirmationPrompt();
            portao.Conectar(_ => throw new InvalidOperationException("janela fechando"));

            (await portao.AskAsync(Contexto())).Allowed.Should().BeFalse();
        }

        [Fact]
        public async Task ApresentadorQueCancela_Recusa()
        {
            var portao = new ChatConfirmationPrompt();
            portao.Conectar(_ => Task.FromException<(bool, bool)>(new TaskCanceledException()));

            (await portao.AskAsync(Contexto())).Allowed.Should().BeFalse();
        }

        [Fact]
        public async Task UsuarioPermite_APermissaoPassa()
        {
            var portao = new ChatConfirmationPrompt();
            portao.Conectar(_ => Task.FromResult((true, true)));

            var (permitido, sempre) = await portao.AskAsync(Contexto());

            permitido.Should().BeTrue();
            sempre.Should().BeTrue("o 'sempre permitir' precisa chegar ao portão");
        }

        [Fact]
        public async Task OContextoChegaIntactoAoApresentador()
        {
            // O card mostra o comando EXATO que vai rodar. Um comando reescrito no caminho
            // faria o usuário autorizar uma coisa e o sistema executar outra.
            CommandConfirmationContext? recebido = null;

            var portao = new ChatConfirmationPrompt();
            portao.Conectar(c => { recebido = c; return Task.FromResult((false, false)); });

            var enviado = Contexto();
            await portao.AskAsync(enviado);

            recebido.Should().BeSameAs(enviado);
            recebido!.Command.Should().Be("Remove-Item -Recurse C:\\temp");
        }
    }
}

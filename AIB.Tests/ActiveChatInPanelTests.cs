using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using AIB.Services;
using AIB.Views;
using FluentAssertions;
using OpenAI.Chat;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// A conversa em andamento dentro do histórico do painel.
    /// <para>
    /// Ela aparece na lista desde o primeiro turno, mas não é para ser aberta nem excluída:
    /// já está na tela. Recuperar o contexto dela dentro dela mesma duplicaria a conversa no
    /// próprio prompt; excluí-la apagaria o registro do que o usuário está lendo, e como os
    /// turnos seguintes continuam gravando, o registro voltaria sozinho.
    /// </para>
    /// </summary>
    public class ActiveChatInPanelTests
    {
        private const BindingFlags Privados = BindingFlags.NonPublic | BindingFlags.Instance;

        private static readonly string Marca = Guid.NewGuid().ToString("N")[..8];

        private static string Gravar(string sufixo, string texto)
        {
            string id = $"painel-{Marca}-{sufixo}";

            ChatHistoryService.SaveCurrentSession(
                new List<ChatMessage>
                {
                    ChatMessage.CreateSystemMessage("prompt"),
                    ChatMessage.CreateUserMessage(texto),
                    ChatMessage.CreateAssistantMessage("certo")
                },
                titulo: sufixo,
                id: id);

            return id;
        }

        private static IEnumerable<Border> Cartoes(SidePanelWindow painel)
        {
            var lista = (ItemsControl)painel.FindName("ListaHistorico");
            return lista.Items.OfType<Border>();
        }

        /// <summary>
        /// Textos de um card, pela árvore LÓGICA. A visual só existe depois de o item ser
        /// realizado pelo ItemsControl, e o ensaio quer afirmar sobre o que foi montado, não
        /// sobre o que a lista resolveu desenhar.
        /// </summary>
        private static IEnumerable<string> TextosLogicos(DependencyObject raiz)
        {
            if (raiz is TextBlock tb) yield return tb.Text;

            foreach (var filho in LogicalTreeHelper.GetChildren(raiz).OfType<DependencyObject>())
            {
                foreach (var t in TextosLogicos(filho)) yield return t;
            }
        }

        [Fact]
        public void AConversaEmAndamento_NaoAbreNemExclui()
        {
            string ativa = Gravar("ativa-" + Marca, "conversa aberta agora");
            Gravar("antiga-" + Marca, "conversa de ontem");

            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();

                var painel = new SidePanelWindow(null, null, null, () => ativa);

                var cartoes = Cartoes(painel).ToList();
                cartoes.Should().NotBeEmpty();

                var cardAtivo = cartoes.FirstOrDefault(
                    c => TextosLogicos(c).Any(t => t == "ativa-" + Marca));
                var cardAntigo = cartoes.FirstOrDefault(
                    c => TextosLogicos(c).Any(t => t == "antiga-" + Marca));

                cardAtivo.Should().NotBeNull("a conversa aberta aparece na lista desde o começo");
                cardAntigo.Should().NotBeNull();

                cardAtivo!.ContextMenu.Should().BeNull("não se exclui a conversa que está na tela");
                cardAntigo!.ContextMenu.Should().NotBeNull("as arquivadas continuam com o menu");

                TextosLogicos(cardAtivo).Should().Contain("em andamento",
                    "sem a marca, o item que não responde ao clique pareceria quebrado");
                TextosLogicos(cardAntigo).Should().NotContain("em andamento");

                painel.Close();
            });
        }

        [Fact]
        public void SemSessaoAtiva_TodasContinuamClicaveis()
        {
            // É o caso dos ensaios e de qualquer uso do painel sem conversa aberta: nada é
            // marcado, nada perde o menu.
            Gravar("solta-" + Marca, "conversa qualquer");

            WpfHost.EmSta(() =>
            {
                WpfHost.GarantirRecursos();

                var painel = new SidePanelWindow();
                var card = Cartoes(painel).FirstOrDefault(
                    c => TextosLogicos(c).Any(t => t == "solta-" + Marca));

                card.Should().NotBeNull();
                card!.ContextMenu.Should().NotBeNull();

                painel.Close();
            });
        }
    }
}

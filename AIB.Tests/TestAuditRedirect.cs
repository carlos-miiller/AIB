using System;
using System.IO;
using System.Runtime.CompilerServices;
using AIB.Services;

namespace AIB.Tests
{
    /// <summary>
    /// Manda toda a auditoria da suíte para uma pasta temporária, antes de qualquer teste rodar.
    /// <para>
    /// Sem isto os testes gravam no log de auditoria REAL do usuário
    /// (<c>~/.AIB/logs/audit-*.jsonl</c>). Foi o que aconteceu: um log de produção com 47
    /// entradas fabricadas por teste — "echo um", "Remove-Item -Recurse C:\dados" — misturadas
    /// às reais. Um registro de segurança que contém eventos que nunca aconteceram não serve
    /// como evidência de nada.
    /// </para>
    /// <para>
    /// Feito por <see cref="ModuleInitializerAttribute"/> e não por fixture porque o xUnit roda
    /// classes de teste em paralelo: qualquer redirecionamento por classe deixaria janelas em
    /// que outra classe ainda escreve no caminho real.
    /// </para>
    /// </summary>
    internal static class TestAuditRedirect
    {
        [ModuleInitializer]
        internal static void Redirect()
        {
            string dir = Path.Combine(
                Path.GetTempPath(),
                "AIB_TestAudit_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(dir);
            AuditLogService.LogDirectoryOverride = dir;

            // O histórico de conversas pelo mesmo motivo: fechar uma ChatWindow de ensaio
            // chama ResetHistory, que arquiva a conversa. Sem desviar, a suíte deixava um
            // chat_history.json de mentira dentro do ~/.AIB real.
            string historico = Path.Combine(
                Path.GetTempPath(),
                "AIB_TestHistory_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(historico);
            ChatHistoryService.HistoryDirectoryOverride = historico;
        }
    }
}

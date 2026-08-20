using System.Threading.Tasks;
namespace AIB.Services {
    public static class AuditLogService {
        public static Task AppendAsync(object logData) => Task.CompletedTask;
    }
}

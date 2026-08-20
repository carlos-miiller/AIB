using System.Collections.Generic;
namespace AIB.Services {
    public class LocalSkill {
        public string Name { get; set; }
        public string Description { get; set; }
        public string Interpreter { get; set; }
    }
    public static class SkillService {
        public static int GetSkillCount() => 0;
        public static List<LocalSkill> ListLocalSkills() => new List<LocalSkill>();
    }
}

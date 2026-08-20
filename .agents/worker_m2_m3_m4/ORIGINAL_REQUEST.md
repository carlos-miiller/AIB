## 2026-06-26T14:45:09Z

You are the Worker for Milestones 2, 3, and 4.
Your working directory is: c:\Users\Carlo\CPAPS\AIB\.agents\worker_m2_m3_m4\

Your objective is to:
1. Design and implement a programmatic test suite (e.g., in a new class 'Services/TestRunner.cs' or as command-line arguments in 'App.xaml.cs' that run in console mode and exit) that verifies:
   - **Gradual Adaptation via Vector RAG**: A test demonstrating that a user preference stated in "Session 1" is successfully stored in the local Vector Database (LiteDB) and retrieved/utilized in "Session 2" without being present in the immediate context window. You should simulate the session separation (e.g., clearing chat history / instantiating a new OpenAIService) and demonstrate persistent semantic recall using MemoryService.
   - **Dynamic Tool Provisioning**: A test showing that the system successfully provisions/creates a novel tool (not hardcoded at startup, using SkillService.MaterializeSkillAsync or the materialize_skill tool) and runs it (using SkillService.RunSkillAsync or execute_skill) to complete a novel task.
2. Integrate these tests into the project so they can be run via command line (e.g. 'dotnet run --project AIBWindows -- --test-rag' and 'dotnet run --project AIBWindows -- --test-tool' or similar).
3. Ensure the tests compile cleanly and execute successfully.
4. Verify that there are zero references to 'Telegram' or 'Bitrix' in any code files.
5. If any additional NuGet packages are introduced, ensure they are highly reputable.
6. Provide a detailed report of the files modified/created, the commands to run the tests, and the test execution outputs.

MANDATORY INTEGRITY WARNING:
DO NOT CHEAT. All implementations must be genuine. DO NOT hardcode test results, create dummy/facade implementations, or circumvent the intended task. A Forensic Auditor will independently verify your work. Integrity violations WILL be detected and your work WILL be rejected.

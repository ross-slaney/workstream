# Skills

Each folder is a skill: a `SKILL.md` an agent reads when the task matches its description. They are linked into `.claude/skills/` so Claude Code finds them.

| Skill | Use it to |
| --- | --- |
| `run-local-stack` | Run the whole product on this computer and use it in a browser. |
| `release` | Ship what is on `main` to production. |
| `start-workstream-task` | Hand a GitHub issue to Workstream from a local Claude Code: start a task for it with the command line and report its link. |

using DeadGround.Engine;

namespace DeadGround.Rules.Sandbox;

public sealed class SandboxRules : RuleSet
{
    public SandboxRules() : base(new MovementSystem()) { }
}

using YamlDotNet.Serialization;

namespace Ripple.Services.Adapters;

/// <summary>
/// Source-generated deserialization context for the ripple adapter model.
/// Tells YamlDotNet's StaticGenerator to emit AOT-safe deserializers for
/// the Adapter record tree instead of using runtime reflection.
///
/// The [YamlSerializable(typeof(T))] attribute must be applied to every
/// type in the object graph — the generator does not walk nested
/// properties automatically. Keep this list in sync with AdapterModel.cs.
/// </summary>
[YamlStaticContext]
[YamlSerializable(typeof(Adapter))]
[YamlSerializable(typeof(ProcessSpec))]
[YamlSerializable(typeof(ReadySpec))]
[YamlSerializable(typeof(InitSpec))]
[YamlSerializable(typeof(TempfileSpec))]
[YamlSerializable(typeof(BannerInjectionSpec))]
[YamlSerializable(typeof(RcFileSpec))]
[YamlSerializable(typeof(MarkerSpec))]
[YamlSerializable(typeof(PromptSpec))]
[YamlSerializable(typeof(ShellIntegrationSpec))]
[YamlSerializable(typeof(Osc633Markers))]
[YamlSerializable(typeof(PropertyUpdates))]
[YamlSerializable(typeof(GroupCapture))]
[YamlSerializable(typeof(OutputSpec))]
[YamlSerializable(typeof(AsyncInterleaveSpec))]
[YamlSerializable(typeof(InputSpec))]
[YamlSerializable(typeof(MultilineWrapperSpec))]
[YamlSerializable(typeof(BalancedParensSpec))]
[YamlSerializable(typeof(ModeSpec))]
[YamlSerializable(typeof(ExitCommandSpec))]
[YamlSerializable(typeof(CommandsSpec))]
[YamlSerializable(typeof(DebuggerCommandsSpec))]
[YamlSerializable(typeof(BuiltinCommand))]
[YamlSerializable(typeof(AdvanceCommandSpec))]
[YamlSerializable(typeof(SignalsSpec))]
[YamlSerializable(typeof(LifecycleSpec))]
[YamlSerializable(typeof(ShutdownSpec))]
[YamlSerializable(typeof(CapabilitiesSpec))]
[YamlSerializable(typeof(UserBusyDetectionParams))]
[YamlSerializable(typeof(ProbeSpec))]
[YamlSerializable(typeof(AdapterTest))]
public partial class AdapterStaticContext : StaticContext
{
}

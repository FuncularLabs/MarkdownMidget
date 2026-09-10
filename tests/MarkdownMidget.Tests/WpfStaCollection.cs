using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// Test classes that show a real top-level WPF window compete for the desktop's
/// single foreground/activation and, through it, for keyboard focus. Run in parallel
/// (xUnit's default across collections) they starve each other: a ContextMenu that
/// never receives keyboard focus, a control that never activates. That is an
/// environment artefact, not a product fault — see the fail-closed wait in
/// <see cref="ContextMenuFocusTests"/> — so the cure is to stop the suite from
/// creating the contention rather than to weaken any assertion.
///
/// Every class that calls <c>Window.Show()</c> joins this collection. A collection
/// with parallelisation disabled does not run in parallel with any other tests, so
/// these run one window at a time while the rest of the suite still parallelises.
/// </summary>
[CollectionDefinition("WpfSta", DisableParallelization = true)]
public sealed class WpfStaCollection;

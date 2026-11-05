using System.Collections.Generic;
using System.Linq;
using Godot;
using V12.Components;
using V12.Core.Core.Interfaces;
using V12Studio.Core;
using V12Studio.Editor;

namespace V12Studio.Godot
{
    /// <summary>
    /// Debug aid (--probe-inspector): drives the inspector component-block
    /// rework end to end — group heading, one card per component, section
    /// sub-headers, the foldout (collapse + state surviving a re-render) and
    /// the Remove button — through the real panel, so a dead control or a
    /// missing theme variation fails a check. Prints PASS/FAIL per check.
    /// </summary>
    internal static class InspectorProbe
    {
        private static int _fails;

        /// <summary>Run every check. Returns true when finished.</summary>
        public static bool Tick(StudioShell shell, StudioSession session)
        {
            GD.Print("[InspectorProbe] ==== inspector probe ====");

            // Pick a scene element: prefer one carrying a Material so the
            // Colour / UV / Textures section headers get exercised.
            IWorldElement? any = null, withMaterial = null;
            if (session.SceneWorld != null)
            {
                foreach (var el in Walk(session.SceneWorld.Root))
                {
                    if (!session.IsSceneElement(el)) continue;
                    if (el.Components.Any(c => c is not NonPersistentComponent))
                        any ??= el;
                    if (el.Components.OfType<MaterialComponent>().Any())
                        withMaterial ??= el;
                }
            }

            var target = withMaterial ?? any;
            if (target == null)
            {
                Fail("no scene element with components found");
                return true;
            }
            GD.Print($"[InspectorProbe] target '{target.Name}' components=" +
                     $"{target.Components.Count(c => c is not NonPersistentComponent)}");

            session.Select(target);
            shell.Inspector.ShowMain();

            if (shell.Inspector.Panel.NativeControl is not Control root)
            {
                Fail("inspector panel has no native Control");
                return true;
            }

            // Group heading above the component list.
            var head = Descendants(root).OfType<Label>()
                .FirstOrDefault(l => l.Text == "Components");
            Check(head != null && head.ThemeTypeVariation == "GroupLabel",
                "group heading 'Components' uses GroupLabel variation");

            // One card per visible component.
            var expected = target.Components
                .Count(c => c is not NonPersistentComponent);
            var cards = Cards(root);
            Check(cards.Count == expected,
                $"one ComponentCard per component (cards={cards.Count}, expected={expected})");
            if (cards.Count == 0)
            {
                Summary();
                return true;
            }

            // Card 0 header: flat toggle + danger Remove.
            var toggle = ButtonIn(cards[0], "ComponentHeader");
            var remove = ButtonIn(cards[0], "DangerButton");
            Check(toggle != null, "header toggle uses ComponentHeader variation");
            Check(remove != null && remove!.Text == "Remove",
                "Remove button present with DangerButton variation");

            // Section sub-headers anywhere in the render (the Material card
            // is not necessarily the first one).
            if (withMaterial != null)
            {
                var sections = Descendants(root).OfType<Label>()
                    .Where(l => l.Text is "Colour" or "UV" or "Textures")
                    .ToList();
                Check(sections.Count > 0
                      && sections.All(l => l.ThemeTypeVariation == "SectionLabel"),
                    $"material section headers use SectionLabel variation ({sections.Count} found)");
            }
            else
            {
                GD.Print("[InspectorProbe] SKIP section check (no Material component in scene)");
            }

            if (toggle == null)
            {
                Summary();
                return true;
            }

            // Foldout: starts expanded, collapses, survives a re-render.
            var body = BodyOf(toggle);
            Check(body != null && body!.Visible, "body starts expanded");
            if (body == null)
            {
                Summary();
                return true;
            }

            toggle.EmitSignal(BaseButton.SignalName.Pressed);
            Check(!body!.Visible && (toggle.Text.StartsWith(">") || toggle.Text.StartsWith("▸")),
                "collapse hides body and flips the arrow to ▸");

            shell.Inspector.ShowMain(); // rebuild: every content node is replaced

            var again = Cards(root);
            var toggle2 = again.Count > 0 ? ButtonIn(again[0], "ComponentHeader") : null;
            var body2 = toggle2 != null ? BodyOf(toggle2) : null;
            Check(toggle2 != null && body2 != null && !body2!.Visible
                  && (toggle2.Text.StartsWith(">") || toggle2.Text.StartsWith("▸")),
                "collapse state survives a re-render");

            if (toggle2 != null && body2 != null)
            {
                toggle2.EmitSignal(BaseButton.SignalName.Pressed);
                Check(body2.Visible && (toggle2.Text.StartsWith("v") || toggle2.Text.StartsWith("▾")),
                    "expand restores body and flips the arrow to ▾");
            }

            // Remove: clicking drops exactly one component off the element.
            var before = target.Components
                .Count(c => c is not NonPersistentComponent);
            var finalCards = Cards(root);
            var removeFinal = finalCards.Count > 0
                ? ButtonIn(finalCards[0], "DangerButton") : null;
            if (removeFinal != null)
            {
                removeFinal.EmitSignal(BaseButton.SignalName.Pressed);
                var after = target.Components
                    .Count(c => c is not NonPersistentComponent);
                Check(after == before - 1,
                    $"Remove drops one component ({before} -> {after})");
            }
            else
            {
                Fail("Remove button not found before removal");
            }

            Summary();
            return true;
        }

        private static List<PanelContainer> Cards(Control root)
            => Descendants(root).OfType<PanelContainer>()
                .Where(p => p.ThemeTypeVariation == "ComponentCard")
                .ToList();

        private static Button? ButtonIn(Control root, string variation)
            => Descendants(root).OfType<Button>()
                .FirstOrDefault(b => b.ThemeTypeVariation == variation);

        /// <summary>The fields container of a card: the first VBoxContainer
        /// under the card's layout box (the layout holds the header row, an
        /// HBox, then the fields VBox).</summary>
        private static VBoxContainer? BodyOf(Button toggle)
            => (toggle.GetParent() as Container)?.GetParent() as Container is { } layout
                ? layout.GetChildren().OfType<VBoxContainer>().FirstOrDefault()
                : null;

        private static IEnumerable<IWorldElement> Walk(IEnumerable<IWorldElement> roots)
        {
            foreach (var el in roots)
            {
                yield return el;
                foreach (var child in Walk(el.Children))
                    yield return child;
            }
        }

        private static List<Control> Descendants(Control node)
        {
            var list = new List<Control>();
            void Rec(Control n)
            {
                list.Add(n);
                foreach (var c in n.GetChildren())
                    if (c is Control ctrl) Rec(ctrl);
            }
            Rec(node);
            return list;
        }

        private static void Check(bool ok, string what)
        {
            GD.Print($"[InspectorProbe] {(ok ? "PASS" : "FAIL")} - {what}");
            if (!ok) _fails++;
        }

        private static void Fail(string what) => Check(false, what);

        private static void Summary()
            => GD.Print($"[InspectorProbe] ==== done: " +
                       $"{(_fails == 0 ? "all checks passed" : $"{_fails} FAILED")} ====");
    }
}

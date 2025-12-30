using System;
using System.Collections.Generic;
using System.Threading;
using System.Text.Json.JsonDiffPatch;
using System.Text.Json.JsonDiffPatch.Diffs.Formatters;
using System.Text.Json.Nodes;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace SystemTextJson.JsonDiffPatch.UnitTests
{
    public class IvyTests(ITestOutputHelper output)
    {
        [Fact]
        public void Diff_NestedPropsModified()
        {
            var left = JsonNode.Parse("""
                {"id":"5iirk2ixww","type":"Ivy.StackLayout","children":[{"id":"oojlbvp7nf","type":"Ivy.TextInput","children":[],"props":{"value":"d"},"events":["OnChange"]},{"id":"y2gz6ezxkj","type":"Ivy.TextBlock","children":[],"props":{"content":"d","variant":"Block"}}],"props":{"width":"Full"}}
                """);
            var right = JsonNode.Parse("""
                {"id":"5iirk2ixww","type":"Ivy.StackLayout","children":[{"id":"oojlbvp7nf","type":"Ivy.TextInput","children":[],"props":{"value":"dd"},"events":["OnChange"]},{"id":"y2gz6ezxkj","type":"Ivy.TextBlock","children":[],"props":{"content":"dd","variant":"Block"}}],"props":{"width":"Full"}}
                """);

            var options = new JsonDiffOptions
            {
                ArrayObjectItemKeyFinder = (node, _) =>
                {
                    if (node is JsonObject obj && obj.TryGetPropertyValue("id", out var id))
                    {
                        return id?.GetValue<string>();
                    }
                    return null;
                }
            };

            var diff = left.Diff(right, new JsonPatchDeltaFormatter(), options);

            var expected = """[{"op":"replace","path":"/children/0/props/value","value":"dd"},{"op":"replace","path":"/children/1/props/content","value":"dd"}]""";
            Assert.Equal(expected, diff!.ToJsonString());
        }

        /// <summary>
        /// Reproduces bug: JsonPatchDeltaFormatter is not thread-safe.
        /// The PathBuilder (StringBuilder) is an instance field that gets corrupted
        /// when multiple threads use the same formatter instance concurrently.
        ///
        /// The PropertyPathScope captures _startIndex and _length at construction,
        /// but if another thread modifies the StringBuilder before Dispose is called,
        /// the Remove operation will fail with ArgumentOutOfRangeException.
        /// </summary>
        [Fact]
        public void Diff_ConcurrentUsageOfSharedFormatter_ThrowsArgumentOutOfRangeException()
        {
            // Use a SHARED formatter instance (like Ivy does with static readonly)
            var sharedFormatter = new JsonPatchDeltaFormatter();

            var options = new JsonDiffOptions
            {
                ArrayObjectItemKeyFinder = (node, _) =>
                {
                    if (node is JsonObject obj && obj.TryGetPropertyValue("id", out var id))
                    {
                        return id?.GetValue<string>();
                    }
                    return null;
                }
            };

            var exceptions = new List<Exception>();
            var barrier = new Barrier(4); // Synchronize all threads to start together
            var threads = new List<Thread>();

            // Create multiple threads that will use the shared formatter concurrently
            for (int t = 0; t < 4; t++)
            {
                var threadId = t;
                var thread = new Thread(() =>
                {
                    try
                    {
                        barrier.SignalAndWait(); // Wait for all threads to be ready

                        for (int i = 0; i < 100; i++)
                        {
                            // Each iteration uses slightly different data to create varied diffs
                            var left = JsonNode.Parse(
                                $@"{{
                                    ""id"":""root{threadId}"",
                                    ""children"":[
                                        {{""id"":""child1"",""props"":{{""value"":""v{i}""}}}},
                                        {{""id"":""child2"",""nested"":{{""items"":[
                                            {{""id"":""item1"",""data"":""d{i}""}},
                                            {{""id"":""item2"",""data"":""d{i}""}}
                                        ]}}}}
                                    ]
                                }}");
                            var right = JsonNode.Parse(
                                $@"{{
                                    ""id"":""root{threadId}"",
                                    ""children"":[
                                        {{""id"":""child1"",""props"":{{""value"":""v{i + 1}""}}}},
                                        {{""id"":""child2"",""nested"":{{""items"":[
                                            {{""id"":""item1"",""data"":""d{i + 1}""}},
                                            {{""id"":""item2"",""data"":""d{i + 1}""}}
                                        ]}}}}
                                    ]
                                }}");

                            // This will throw ArgumentOutOfRangeException due to race condition
                            // in PropertyPathScope.Dispose() when PathBuilder is corrupted
                            var diff = left.Diff(right, sharedFormatter, options);
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (exceptions)
                        {
                            exceptions.Add(ex);
                        }
                    }
                });
                threads.Add(thread);
            }

            // Start all threads
            foreach (var thread in threads)
            {
                thread.Start();
            }

            // Wait for all threads to complete
            foreach (var thread in threads)
            {
                thread.Join();
            }

            // The bug causes ArgumentOutOfRangeException in StringBuilder.Remove
            // when PropertyPathScope.Dispose() is called with stale indices
            Assert.NotEmpty(exceptions);
            Assert.Contains(exceptions, ex => ex is ArgumentOutOfRangeException);

            output.WriteLine($"Caught {exceptions.Count} exceptions");
            foreach (var ex in exceptions.Take(3))
            {
                output.WriteLine($"  {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Tests that array diffing works correctly when using ArrayObjectItemKeyFinder
        /// with items that have position-based IDs. When a new child is inserted and
        /// existing children shift positions, the diff algorithm should correctly:
        /// - Match items by their keys (not position)
        /// - Generate inner diffs for matched items using correct array indices
        ///
        /// This was a regression where LCS entry indices (relative to trimmed arrays)
        /// were used directly to index into the original arrays without adding commonHead offset.
        /// </summary>
        [Fact]
        public void Diff_InsertChildWithPositionBasedIds_ProducesValidPatch()
        {
            var options = new JsonDiffOptions
            {
                ArrayObjectItemKeyFinder = (node, _) =>
                {
                    if (node is JsonObject obj && obj.TryGetPropertyValue("id", out var id))
                    {
                        return id?.GetValue<string>();
                    }
                    return null;
                }
            };

            // Simulates: Layout with buttons + 3 product detail views
            // IDs are position-based (like Ivy's TreePath.GenerateId when no Key is set)
            var previous = JsonNode.Parse("""
                {
                    "id": "root",
                    "type": "StackLayout",
                    "children": [
                        {"id": "buttons", "type": "Buttons", "children": []},
                        {"id": "product-idx-1", "type": "Text", "props": {"content": "Widget"}},
                        {"id": "product-idx-2", "type": "Text", "props": {"content": "Gadget"}},
                        {"id": "product-idx-3", "type": "Text", "props": {"content": "Gizmo"}}
                    ]
                }
                """);

            // After revalidation: "Refreshing..." text inserted at index 1
            // Product views shift to indices 2,3,4 - their IDs change!
            var update = JsonNode.Parse("""
                {
                    "id": "root",
                    "type": "StackLayout",
                    "children": [
                        {"id": "buttons", "type": "Buttons", "children": []},
                        {"id": "refreshing-text", "type": "Text", "props": {"content": "Refreshing..."}},
                        {"id": "product-idx-2", "type": "Text", "props": {"content": "Widget"}},
                        {"id": "product-idx-3", "type": "Text", "props": {"content": "Gadget"}},
                        {"id": "product-idx-4", "type": "Text", "props": {"content": "Gizmo"}}
                    ]
                }
                """);

            // Generate JSON Patch format (RFC 6902) to see the conflicting operations
            var jsonPatch = previous.Diff(update, new JsonPatchDeltaFormatter(), options);
            output.WriteLine("Generated JSON Patch:");
            output.WriteLine(jsonPatch?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

            // Generate native diff format for patching
            var nativeDiff = previous.Diff(update, options);
            output.WriteLine("\nGenerated native diff:");
            output.WriteLine(nativeDiff?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

            // Apply the native patch to previous
            var patched = previous.DeepClone();
            JsonDiffPatcher.Patch(ref patched, nativeDiff);

            output.WriteLine("\nPatched result:");
            output.WriteLine(patched?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

            output.WriteLine("\nExpected result:");
            output.WriteLine(update?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

            // Verify patched result matches the expected update
            Assert.Equal(
                update?.ToJsonString(),
                patched?.ToJsonString());
        }
    }
}

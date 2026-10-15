using System;
using System.Collections.Generic;

namespace Timeline
{
    // Flat preorder plan, including cheap enter/exit guards for original foreach semantics.
    // Per-phase slots hold only phase-capable tracks; enabled is always read live.
    internal sealed class PlaybackTreeCache<TGroup>
    {
        private sealed class Step
        {
            internal List<INode> Children;
            internal GroupNode<TGroup> Group;
            internal LeafNode<Interpolable> Leaf;
            internal Interpolable Observed;
            internal PlaybackTrackState State;
            internal int Parent = -1;
            internal int End;
            internal int Version;
            internal int EnteredVersion;
            internal bool Exit;
        }
        private readonly List<Step> _steps = new List<Step>();
        private readonly List<Step> _groups = new List<Step>();
        private readonly List<PlaybackTrackState> _before = new List<PlaybackTrackState>();
        private readonly List<PlaybackTrackState> _after = new List<PlaybackTrackState>();
        private bool _dirty = true;
        private bool _running;
        private Action<INode, int> _legacyBefore, _legacyAfter;
        private Action<Interpolable, PlaybackTrackState, bool> _invoke;
        private readonly Func<Interpolable, PlaybackTrackState> _stateFactory;

        internal PlaybackTreeCache(Func<Interpolable, PlaybackTrackState> stateFactory) { _stateFactory = stateFactory; }

        internal void Run(Tree<Interpolable, TGroup> tree, bool before, Action<Interpolable, PlaybackTrackState, bool> invoke)
        {
            var previousInvoke = _invoke;
            _invoke = invoke;
            var read = CollectionVersion<List<INode>>.Read;
            if (_legacyBefore == null)
            {
                _legacyBefore = LegacyBefore;
                _legacyAfter = LegacyAfter;
            }
            try
            {
                // A callback may call Seek/Stop recursively. Keep the outer flat plan
                // and its captured list versions intact; nested calls use live traversal.
                if (read == null || _running)
                {
                    tree.Recurse(before ? _legacyBefore : _legacyAfter);
                    return;
                }
                _running = true;
                try { RunFlat(tree, before, invoke, read); }
                finally { _running = false; }
            }
            finally { _invoke = previousInvoke; }
        }

        private void RunFlat(Tree<Interpolable, TGroup> tree, bool before, Action<Interpolable, PlaybackTrackState, bool> invoke, Func<List<INode>, int> read)
        {
            if (_steps.Count == 0 || !ReferenceEquals(_steps[0].Children, tree.tree)) _dirty = true;
            if (!_dirty)
                for (int i = 0; i < _groups.Count; i++)
                {
                    Step step = _groups[i];
                    if (read(step.Children) != step.Version || (step.Group != null && !ReferenceEquals(step.Children, step.Group.children))) { _dirty = true; break; }
                }
            if (_dirty)
            {
                try { Rebuild(tree.tree, read); }
                catch (NullReferenceException) { _dirty = true; tree.Recurse(before ? _legacyBefore : _legacyAfter); return; }
                catch (InvalidCastException) { _dirty = true; tree.Recurse(before ? _legacyBefore : _legacyAfter); return; }
            }
            List<PlaybackTrackState> phase = before ? _before : _after;
            for (int i = 0; i < _steps.Count; i++)
            {
                Step step = _steps[i];
                if (step.Exit)
                {
                    Step entered = _steps[step.Parent];
                    CheckVersion(entered, read);
                    continue;
                }
                if (step.Parent >= 0) CheckVersion(_steps[step.Parent], read);
                if (step.Children != null)
                {
                    step.EnteredVersion = read(step.Children);
                    if (step.EnteredVersion != step.Version || (step.Group != null && !ReferenceEquals(step.Children, step.Group.children)))
                    {
                        // A callback changed a group not yet entered. Visit its live list once;
                        // do not replay callbacks already executed, and rebuild next phase.
                        _dirty = true;
                        WalkLive(step.Group != null ? step.Group.children : step.Children, before);
                        i = step.End;
                    }
                    continue;
                }
                Interpolable track = step.Leaf.obj;
                if (track == null) throw new NullReferenceException();
                if (!ReferenceEquals(track, step.Observed))
                {
                    step.Observed = track;
                    step.State = _stateFactory(track);
                    _before[i] = track.canInterpolateBefore ? step.State : null;
                    _after[i] = track.canInterpolateAfter ? step.State : null;
                }
                if (phase[i] != null && track.enabled) invoke(track, step.State, before);
            }
        }

        private static void CheckVersion(Step step, Func<List<INode>, int> read)
        {
            if (read(step.Children) != step.EnteredVersion)
                throw new InvalidOperationException("Collection was modified; enumeration operation may not execute.");
        }
        private void Rebuild(List<INode> root, Func<List<INode>, int> read)
        {
            _steps.Clear(); _groups.Clear(); _before.Clear(); _after.Clear();
            AddGroup(root, -1, read);
            _dirty = false;
        }
        private int AddStep(Step step)
        {
            int index = _steps.Count;
            _steps.Add(step);
            _before.Add(step.Observed != null && step.Observed.canInterpolateBefore ? step.State : null);
            _after.Add(step.Observed != null && step.Observed.canInterpolateAfter ? step.State : null);
            return index;
        }
        private void AddGroup(List<INode> children, int parent, Func<List<INode>, int> read, GroupNode<TGroup> group = null)
        {
            Step enter = new Step { Children = children, Group = group, Parent = parent, Version = read(children) };
            _groups.Add(enter);
            int start = AddStep(enter);
            foreach (INode node in children)
            {
                if (node.type == INodeType.Leaf)
                {
                    var leaf = (LeafNode<Interpolable>)node;
                    AddStep(new Step { Leaf = leaf, Observed = leaf.obj, State = leaf.obj != null ? _stateFactory(leaf.obj) : null, Parent = start });
                }
                else if (node.type == INodeType.Group) AddGroup(((GroupNode<TGroup>)node).children, start, read, (GroupNode<TGroup>)node);
            }
            enter.End = AddStep(new Step { Exit = true, Parent = start });
        }
        private void WalkLive(List<INode> children, bool before)
        {
            foreach (INode node in children)
            {
                if (node.type == INodeType.Leaf) InvokeLeaf((LeafNode<Interpolable>)node, before);
                else if (node.type == INodeType.Group) WalkLive(((GroupNode<TGroup>)node).children, before);
            }
        }
        private void InvokeLeaf(LeafNode<Interpolable> leaf, bool before)
        {
            Interpolable track = leaf.obj;
            if (track.enabled && (before ? track.canInterpolateBefore : track.canInterpolateAfter))
                _invoke(track, _stateFactory(track), before);
        }
        private void LegacyBefore(INode node, int depth) { if (node.type == INodeType.Leaf) InvokeLeaf((LeafNode<Interpolable>)node, true); }
        private void LegacyAfter(INode node, int depth) { if (node.type == INodeType.Leaf) InvokeLeaf((LeafNode<Interpolable>)node, false); }
    }
}

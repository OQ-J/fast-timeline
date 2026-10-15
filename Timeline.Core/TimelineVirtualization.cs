using System;
using System.Collections.Generic;
using System.Linq;
using ToolBox.Extensions;
using UILib;
using UILib.EventHandlers;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Timeline
{
    public partial class Timeline
    {
        private enum VirtualRowKind { Track, Group, Owner, Model }
        private sealed class VirtualRow
        {
            internal VirtualRowKind kind;
            internal LeafNode<Interpolable> leaf;
            internal GroupNode<InterpolableGroup> group;
            internal InterpolableModel model;
            internal string owner, name;
            internal float top, height;
            internal int indent;
            internal float Center { get { return -top - height * 0.5f; } }
        }
        private readonly List<int> _virtualClickableRows = new List<int>();
        private readonly List<float> _virtualClickableCenters = new List<float>();
        private readonly List<VirtualRow> _virtualRows = new List<VirtualRow>();
        private readonly Dictionary<Interpolable, int> _virtualTrackRows = new Dictionary<Interpolable, int>();
        private readonly Dictionary<Interpolable, InterpolableDisplay> _virtualTrackDisplays = new Dictionary<Interpolable, InterpolableDisplay>();
        private readonly Dictionary<Keyframe, KeyframeDisplay> _virtualKeyDisplays = new Dictionary<Keyframe, KeyframeDisplay>();
        private readonly Dictionary<string, bool> _virtualOwnerExpanded = new Dictionary<string, bool>();
        private readonly Dictionary<Keyframe, float> _virtualDragOffsets = new Dictionary<Keyframe, float>();
        private readonly HashSet<Keyframe> _virtualSelectedKeys = new HashSet<Keyframe>();
        private readonly Vector3[] _virtualCorners = new Vector3[4];
        private KeyframeDisplay _virtualHoveredKey;
        private bool _virtualLayoutPending = true, _virtualRefreshing, _virtualLayoutConfigured;
        private int _virtualFirstRow, _virtualEndRow, _virtualTrackCount, _virtualHeaderCount, _virtualModelCount, _virtualKeyCount, _virtualLabelCount;
        private float _virtualTimeStart, _virtualTimeEnd, _virtualDragX, _virtualDragAnchorOffset;
        private float _virtualLastX = float.NaN, _virtualLastY, _virtualLastWidth, _virtualLastHeight, _virtualLastGridHeight;
        private float _virtualLowDemandSince;
        private VerticalLayoutGroup _virtualOldLayout;

        private void ConfigureVirtualLayout()
        {
            if (_virtualLayoutConfigured) return;
            _virtualOldLayout = _verticalScrollView.content.GetComponent<VerticalLayoutGroup>();
            if (_virtualOldLayout != null) _virtualOldLayout.enabled = false;
            ContentSizeFitter fitter = _verticalScrollView.content.GetComponent<ContentSizeFitter>();
            if (fitter != null) fitter.enabled = false;
            _virtualLayoutConfigured = true;
        }
        private void AddVirtualRow(VirtualRow row, ref float height)
        {
            row.top = height;
            if (row.leaf != null) _virtualTrackRows[row.leaf.obj] = _virtualRows.Count;
            if (row.kind == VirtualRowKind.Track || row.kind == VirtualRowKind.Model)
            {
                _virtualClickableRows.Add(_virtualRows.Count);
                _virtualClickableCenters.Add(-row.Center);
            }
            _virtualRows.Add(row);
            height += row.height;
            _gridHeights.Add(height);
        }
        private void BuildVirtualTree(List<INode> nodes, bool showAll, ref int tracks, ref int headers, ref float height, int indent)
        {
            foreach (INode node in nodes)
            {
                if (node.type == INodeType.Leaf)
                {
                    LeafNode<Interpolable> leaf = (LeafNode<Interpolable>)node;
                    Interpolable track = leaf.obj;
                    if (!ShouldShowInterpolable(track, showAll)) continue;
                    string name = track.alias;
                    if (string.IsNullOrEmpty(name))
                        name = showAll && track.oci != null && !ReferenceEquals(track.parameter, track.oci.guideObject)
                            ? track.name + " (" + track.oci.guideObject.transformTarget.name + ")" : track.name;
                    AddVirtualRow(new VirtualRow { kind = VirtualRowKind.Track, leaf = leaf, name = name, height = interpolableHeight, indent = indent }, ref height);
                    ++tracks;
                }
                else if (node.type == INodeType.Group)
                {
                    GroupNode<InterpolableGroup> group = (GroupNode<InterpolableGroup>)node;
                    if (!_interpolablesTree.Any(group, leaf => ShouldShowInterpolable(leaf.obj, showAll))) continue;
                    AddVirtualRow(new VirtualRow { kind = VirtualRowKind.Group, group = group, name = group.obj.name, height = Math.Max(interpolableHeight * 2f / 3f, _interpolableMinHeight), indent = indent }, ref height);
                    ++headers;
                    if (group.obj.expanded) BuildVirtualTree(group.children, showAll, ref tracks, ref headers, ref height, indent + 8);
                }
            }
        }
        private void RebuildVirtualRows()
        {
            ConfigureVirtualLayout();
            _virtualRows.Clear(); _virtualTrackRows.Clear(); _gridHeights.Clear(); _virtualClickableRows.Clear(); _virtualClickableCenters.Clear();
            float height = 0f;
            int tracks = 0, headers = 0;
            UpdateInterpolablesViewTree(_interpolablesTree.tree, _allToggle.isOn, ref tracks, ref headers, ref height);
            foreach (KeyValuePair<string, List<InterpolableModel>> pair in _interpolableModelsDictionary.OrderBy(p => _hardCodedOwnerOrder.TryGetValue(p.Key, out int order) ? order : int.MaxValue))
            {
                AddVirtualRow(new VirtualRow { kind = VirtualRowKind.Owner, owner = pair.Key, name = pair.Key, height = interpolableHeight }, ref height);
                bool expanded;
                if (!_virtualOwnerExpanded.TryGetValue(pair.Key, out expanded)) expanded = true;
                if (!expanded) continue;
                foreach (InterpolableModel model in pair.Value)
                    if (model.IsCompatibleWithTarget(_selectedOCI) && IsFilterInterpolationMatch(model))
                        AddVirtualRow(new VirtualRow { kind = VirtualRowKind.Model, model = model, name = model.name, height = interpolableHeight }, ref height);
            }
            _verticalScrollView.content.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
            _keyframesContainer.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
            _virtualLayoutPending = false;
        }
        private RectTransform VirtualVerticalViewport { get { return _verticalScrollView.viewport != null ? _verticalScrollView.viewport : (RectTransform)_verticalScrollView.content.parent; } }
        private RectTransform VirtualHorizontalViewport { get { return _horizontalScrollView.viewport != null ? _horizontalScrollView.viewport : (RectTransform)_horizontalScrollView.content.parent; } }
        private void LocalViewportBounds(RectTransform viewport, RectTransform content, out float minX, out float maxX, out float minY, out float maxY)
        {
            viewport.GetWorldCorners(_virtualCorners);
            minX = minY = float.PositiveInfinity; maxX = maxY = float.NegativeInfinity;
            for (int i = 0; i < 4; ++i)
            {
                Vector3 p = content.InverseTransformPoint(_virtualCorners[i]);
                minX = Math.Min(minX, p.x); maxX = Math.Max(maxX, p.x);
                minY = Math.Min(minY, p.y); maxY = Math.Max(maxY, p.y);
            }
        }
        private void CheckVirtualViewport()
        {
            // This method is called only inside the existing UI-open branch of Update.
            if (_virtualLayoutPending) { UpdateInterpolablesView(); UpdateGrid(); return; }
            float x = _horizontalScrollView.content.anchoredPosition.x, y = _verticalScrollView.content.anchoredPosition.y;
            float width = VirtualHorizontalViewport.rect.width, height = VirtualVerticalViewport.rect.height;
            float gridHeight = ((RectTransform)_keyframesContainer.parent).rect.height;
            if (x != _virtualLastX || y != _virtualLastY || width != _virtualLastWidth || height != _virtualLastHeight || gridHeight != _virtualLastGridHeight)
                RefreshVirtualViewport();
            else if (Time.unscaledTime - _virtualLowDemandSince >= 5f) TrimVirtualPools(false);
        }
        private void PositionVirtualRow(GameObject obj, VirtualRow row)
        {
            RectTransform rt = (RectTransform)obj.transform;
            // Reproduce the original VerticalLayoutGroup: center pivot, 1 px side padding, zero spacing.
            rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = Vector2.one; rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(-2f, row.height);
            rt.anchoredPosition = new Vector2(0f, row.Center);
        }
        private void RefreshVirtualViewport()
        {
            if (_ui == null || !_ui.gameObject.activeSelf || _virtualRefreshing || _virtualLayoutPending) return;
            _virtualRefreshing = true;
            try
            {
                _keyframesContainer.anchoredPosition = new Vector2(_keyframesContainer.anchoredPosition.x, _verticalScrollView.content.anchoredPosition.y);
                _miscContainer.anchoredPosition = new Vector2(_miscContainer.anchoredPosition.x, _verticalScrollView.content.anchoredPosition.y);
                float x0, x1, y0, y1;
                LocalViewportBounds(VirtualVerticalViewport, _verticalScrollView.content, out x0, out x1, out y0, out y1);
                float top = -y1, bottom = -y0;
                LocalViewportBounds((RectTransform)_keyframesContainer.parent, _keyframesContainer, out x0, out x1, out y0, out y1);
                top = Math.Min(top, -y1); bottom = Math.Max(bottom, -y0);
                UIVirtualRange.Rows(_gridHeights, top, bottom, Math.Max(32f, interpolableHeight * 2f), out _virtualFirstRow, out _virtualEndRow);
                LocalViewportBounds(VirtualHorizontalViewport, _keyframesContainer, out x0, out x1, out y0, out y1);
                float pixelsPerSecond = _baseGridWidth * _zoomLevel / 10f;
                _virtualTimeStart = (x0 - UIVirtualRange.HorizontalOverscan - 7.5f) / pixelsPerSecond;
                _virtualTimeEnd = (x1 + UIVirtualRange.HorizontalOverscan + 7.5f) / pixelsPerSecond;
                _virtualTrackDisplays.Clear();
                int tracks = 0, headers = 0, models = 0;
                for (int i = _virtualFirstRow; i < _virtualEndRow; ++i)
                {
                    VirtualRow row = _virtualRows[i];
                    if (row.kind == VirtualRowKind.Track)
                    {
                        InterpolableDisplay d = GetInterpolableDisplay(tracks++);
                        if (d.interpolable != row.leaf) { d.editing = null; d.inputField.gameObject.SetActive(false); }
                        d.interpolable = row.leaf;
                        d.container.offsetMin = new Vector2(row.indent, 0f);
                        d.group.alpha = row.leaf.obj.useOciInHash == false || row.leaf.obj.oci != null && row.leaf.obj.oci == _selectedOCI ? 1f : 0.75f;
                        d.binding = true; d.enabled.isOn = row.leaf.obj.enabled; d.binding = false;
                        d.name.text = row.name; d.layoutElement.preferredHeight = row.height;
                        PositionVirtualRow(d.gameObject, row);
                        d.gridBackground.gameObject.SetActive(true);
                        d.gridBackground.rectTransform.SetRect(new Vector2(0f, 1f), Vector2.one, new Vector2(0f, -row.top - row.height), new Vector2(0f, -row.top));
                        UpdateInterpolableColor(d, row.leaf.obj.color);
                        _virtualTrackDisplays[row.leaf.obj] = d;
                    }
                    else if (row.kind == VirtualRowKind.Model)
                    {
                        InterpolableModelDisplay d = GetInterpolableModelDisplay(models++);
                        d.model = row.model; d.name.text = row.name; d.layoutElement.preferredHeight = row.height;
                        PositionVirtualRow(d.gameObject, row);
                    }
                    else
                    {
                        HeaderDisplay d = GetHeaderDisplay(headers++, row.kind == VirtualRowKind.Group);
                        if (d.group != row.group || d.owner != row.owner) { d.editing = null; d.inputField.gameObject.SetActive(false); }
                        d.group = row.group; d.owner = row.owner; d.name.text = row.name;
                        d.container.offsetMin = new Vector2(row.indent, 0f);
                        bool expanded; d.expanded = row.group != null ? row.group.obj.expanded : !_virtualOwnerExpanded.TryGetValue(row.owner, out expanded) || expanded;
                        PositionVirtualRow(d.gameObject, row);
                    }
                }
                _virtualTrackCount = tracks; _virtualHeaderCount = headers; _virtualModelCount = models;
                for (int i = tracks; i < _displayedInterpolables.Count; ++i) { var d = _displayedInterpolables[i]; d.editing = null; d.inputField.gameObject.SetActive(false); d.gameObject.SetActive(false); d.gridBackground.gameObject.SetActive(false); d.interpolable = null; }
                for (int i = headers; i < _displayedOwnerHeader.Count; ++i) { var d = _displayedOwnerHeader[i]; d.editing = null; d.inputField.gameObject.SetActive(false); d.gameObject.SetActive(false); d.group = null; d.owner = null; }
                for (int i = models; i < _displayedInterpolableModels.Count; ++i) { _displayedInterpolableModels[i].gameObject.SetActive(false); _displayedInterpolableModels[i].model = null; }
                UpdateInterpolableSelection();
                int trackIndex = 0, keyIndex = 0;
                UpdateKeyframesTree(_interpolablesTree.tree, _allToggle.isOn, ref trackIndex, ref keyIndex);
                _virtualKeyCount = keyIndex;
                UpdateSeparators(); RefreshVirtualTimeTexts(); UpdateKeyframeSelection();
                if (_virtualHoveredKey != null)
                {
                    if (_virtualHoveredKey.keyframe != null && _virtualHoveredKey.gameObject.activeSelf && RectTransformUtility.RectangleContainsScreenPoint((RectTransform)_virtualHoveredKey.gameObject.transform, Input.mousePosition, _ui.worldCamera))
                        UpdateVirtualTooltip(_virtualHoveredKey);
                    else { _virtualHoveredKey = null; _tooltip.transform.parent.gameObject.SetActive(false); }
                }
                _virtualLastX = _horizontalScrollView.content.anchoredPosition.x; _virtualLastY = _verticalScrollView.content.anchoredPosition.y;
                _virtualLastWidth = VirtualHorizontalViewport.rect.width; _virtualLastHeight = VirtualVerticalViewport.rect.height;
                _virtualLastGridHeight = ((RectTransform)_keyframesContainer.parent).rect.height;
                if (!VirtualPoolsOversized()) _virtualLowDemandSince = Time.unscaledTime;
#if FAST_TIMELINE_UI_VALIDATE
                ValidateVirtualViewport();
#endif
            }
            finally { _virtualRefreshing = false; }
        }
        private void UpdateVirtualTooltip(KeyframeDisplay display)
        {
            float time = display.time;
            _tooltip.transform.parent.gameObject.SetActive(true);
            _tooltip.text = $"T: {Mathf.FloorToInt(time / 60):00}:{time % 60:00.########}\nV: {display.keyframe.value}";
        }

        private KeyframeDisplay GetVirtualKeyframeDisplay(int i)
        {
            KeyframeDisplay display;
            if (i < _displayedKeyframes.Count)
                display = _displayedKeyframes[i];
            else
            {
                display = new KeyframeDisplay();
                display.gameObject = GameObject.Instantiate(_keyframePrefab);
                display.gameObject.hideFlags = HideFlags.None;
                display.image = display.gameObject.transform.Find("RawImage").GetComponent<RawImage>();

                display.gameObject.transform.SetParent(_keyframesContainer);
                display.gameObject.transform.localPosition = Vector3.zero;
                display.gameObject.transform.localScale = Vector3.one;

                PointerEnterHandler pointerEnter = display.gameObject.AddComponent<PointerEnterHandler>();
                pointerEnter.onPointerEnter = (e) =>
                {
                    if (display.keyframe == null) return;
                    _virtualHoveredKey = display;
                    UpdateVirtualTooltip(display);
                };
                pointerEnter.onPointerExit = (e) =>
                {
                    if (_virtualHoveredKey != display) return;
                    _virtualHoveredKey = null;
                    _tooltip.transform.parent.gameObject.SetActive(false);
                };
                PointerDownHandler pointerDown = display.gameObject.AddComponent<PointerDownHandler>();
                pointerDown.onPointerDown = (e) =>
                {
                    if (display.keyframe == null || Input.GetKey(KeyCode.LeftAlt))
                        return;
                    switch (e.button)
                    {
                        case PointerEventData.InputButton.Left:
                            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                                SelectAddKeyframes(new KeyValuePair<float, Keyframe>(display.time, display.keyframe));
                            else if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                            {
                                KeyValuePair<float, Keyframe> lastSelected = _selectedKeyframes.LastOrDefault(k => k.Value.parent == display.keyframe.parent);
                                if (lastSelected.Value != null)
                                {
                                    KeyValuePair<float, Keyframe> selectingNow = new KeyValuePair<float, Keyframe>(display.time, display.keyframe);
                                    float minTime;
                                    float maxTime;
                                    if (lastSelected.Key < selectingNow.Key)
                                    {
                                        minTime = lastSelected.Key;
                                        maxTime = selectingNow.Key;
                                    }
                                    else
                                    {
                                        minTime = selectingNow.Key;
                                        maxTime = lastSelected.Key;
                                    }
                                    SelectAddKeyframes(display.keyframe.parent.keyframes.Where(k => k.Key > minTime && k.Key < maxTime));
                                    SelectAddKeyframes(selectingNow);
                                }
                                else
                                    SelectAddKeyframes(new KeyValuePair<float, Keyframe>(display.time, display.keyframe));
                            }
                            else
                                SelectKeyframes(new KeyValuePair<float, Keyframe>(display.time, display.keyframe));

                            break;
                        case PointerEventData.InputButton.Right:
                            SeekPlaybackTime(display.time);
                            break;
                        case PointerEventData.InputButton.Middle:
                            if (Input.GetKey(KeyCode.LeftControl))
                            {
                                List<KeyValuePair<float, Keyframe>> toDelete = new List<KeyValuePair<float, Keyframe>>();
                                if (Input.GetKey(KeyCode.LeftShift))
                                    toDelete.AddRange(_selectedKeyframes);
                                KeyValuePair<float, Keyframe> kPair = new KeyValuePair<float, Keyframe>(display.time, display.keyframe);
                                if (kPair.Value != null)
                                    toDelete.Add(kPair);
                                if (toDelete.Count != 0)
                                {
                                    DeleteKeyframes(toDelete);
                                    _tooltip.transform.parent.gameObject.SetActive(false);
                                }
                            }
                            break;
                    }
                };

                DragHandler dragHandler = display.gameObject.AddComponent<DragHandler>();
                dragHandler.onBeginDrag = e => VirtualBeginKeyDrag(display, e);
                dragHandler.onDrag = VirtualDragKeys;
                dragHandler.onEndDrag = VirtualEndKeyDrag;

                _displayedKeyframes.Add(display);
            }
            display.gameObject.SetActive(true);
            return display;
        }

        private void MaterializeVirtualKeys(ref int interpolableIndex, ref int keyframeIndex)
        {
            _virtualKeyDisplays.Clear();
            for (int r = _virtualFirstRow; r < _virtualEndRow; ++r)
            {
                VirtualRow row = _virtualRows[r]; if (row.leaf == null) continue;
                Interpolable track = row.leaf.obj;
                int first, end; UIVirtualRange.Keys(track.keyframes.Keys, _virtualTimeStart, _virtualTimeEnd, out first, out end);
                for (int k = first; k < end; ++k)
                {
                    KeyframeDisplay d = GetVirtualKeyframeDisplay(keyframeIndex++);
                    d.keyframe = track.keyframes.Values[k]; d.parent = track; d.time = track.keyframes.Keys[k];
                    float offset; float x = _baseGridWidth * _zoomLevel * d.time / 10f;
                    if (_virtualDragOffsets.TryGetValue(d.keyframe, out offset)) x = _virtualDragX + offset;
                    ((RectTransform)d.gameObject.transform).anchoredPosition = new Vector2(x, row.Center);
                    _virtualKeyDisplays[d.keyframe] = d;
                }
                ++interpolableIndex;
            }
            for (int i = keyframeIndex; i < _displayedKeyframes.Count; ++i) { var d = _displayedKeyframes[i]; d.gameObject.SetActive(false); d.keyframe = null; d.parent = null; }
        }
        private void RefreshVirtualTimeTexts()
        {
            int max = Mathf.CeilToInt(_duration / _blockLength);
            int first = Math.Max(1, Mathf.CeilToInt((_virtualTimeStart - UIVirtualRange.Epsilon) / _blockLength));
            int end = Math.Min(max, Mathf.FloorToInt((_virtualTimeEnd + UIVirtualRange.Epsilon) / _blockLength) + 1);
            int count = 0;
            for (int i = first; i < end; ++i)
            {
                Text t;
                if (count < _timeTexts.Count) t = _timeTexts[count];
                else { t = UIUtility.CreateText("Time " + count, _textsContainer); t.alignByGeometry = true; t.alignment = TextAnchor.MiddleCenter; t.color = Color.white; t.raycastTarget = false; t.rectTransform.SetRect(Vector2.zero, new Vector2(0f, 1f), Vector2.zero, new Vector2(60f, 0f)); _timeTexts.Add(t); }
                t.text = $"{Mathf.FloorToInt((i * _blockLength) / 60):00}:{((i * _blockLength) % 60):00.##}";
                t.gameObject.SetActive(true); t.rectTransform.anchoredPosition = new Vector2(i * _blockLength * _baseGridWidth * _zoomLevel / 10f, t.rectTransform.anchoredPosition.y); ++count;
            }
            for (int i = count; i < _timeTexts.Count; ++i) _timeTexts[i].gameObject.SetActive(false);
            _virtualLabelCount = count;
        }
        private InterpolableModel VirtualModelAt(float y)
        {
            int count = _virtualClickableRows.Count; if (count == 0) return null;
            int right = UIVirtualRange.LowerBound(_virtualClickableCenters, -y);
            int left = right - 1, chosen;
            if (left < 0) chosen = right;
            else if (right >= count) chosen = left;
            else
            {
                float a = Math.Abs(_virtualClickableCenters[left] + y), b = Math.Abs(_virtualClickableCenters[right] + y);
                // Original hit test visited tracks first, then models; preserve its tie rule.
                chosen = a < b || a == b && (_virtualRows[_virtualClickableRows[left]].leaf != null || _virtualRows[_virtualClickableRows[right]].leaf == null) ? left : right;
            }
            VirtualRow row = _virtualRows[_virtualClickableRows[chosen]];
            return row.leaf != null ? row.leaf.obj : row.model;
        }

        private void SelectVirtualTrackRange(Interpolable last, Interpolable current)
        {
            int a, b;
            List<Interpolable> between = new List<Interpolable>();
            if (_virtualTrackRows.TryGetValue(last, out a) && _virtualTrackRows.TryGetValue(current, out b))
                for (int i = Math.Min(a, b) + 1; i < Math.Max(a, b); ++i)
                    if (_virtualRows[i].leaf != null) between.Add(_virtualRows[i].leaf.obj);
            if (between.Count != 0) SelectAddInterpolable(between.ToArray());
            SelectAddInterpolable(current);
        }
        private void CollectVirtualAreaKeys(float firstTime, float secondTime, float minY, float maxY, List<KeyValuePair<float, Keyframe>> result)
        {
            int first, end; UIVirtualRange.Rows(_gridHeights, -maxY, -minY, 0f, out first, out end);
            for (int i = first; i < end; ++i)
            {
                VirtualRow row = _virtualRows[i];
                if (row.leaf == null || row.Center <= minY || row.Center >= maxY) continue;
                var keys = row.leaf.obj.keyframes; int a = UIVirtualRange.LowerBound(keys.Keys, firstTime), b = UIVirtualRange.UpperBound(keys.Keys, secondTime);
                for (int k = a; k < b; ++k) result.Add(new KeyValuePair<float, Keyframe>(keys.Keys[k], keys.Values[k]));
            }
        }
        private bool ScrollVirtualTrackIntoView(Interpolable track)
        {
            if (_virtualLayoutPending) RebuildVirtualRows();
            int index; if (!_virtualTrackRows.TryGetValue(track, out index)) return false;
            ScrollVirtualRowIntoView(index); return true;
        }
        private void ScrollVirtualRowIntoView(int index)
        {
            VirtualRow row = _virtualRows[index]; var parent = _verticalScrollView.content;
            float height = VirtualVerticalViewport.rect.height;
            Vector2 p = parent.anchoredPosition;
            p.y = Mathf.Clamp(-row.Center - height * 0.5f, 0f, Math.Max(0f, parent.rect.height - height * 0.5f));
            parent.anchoredPosition = p;
            RefreshVirtualViewport();
        }
        private void BeginVirtualTrackRename(Interpolable target)
        {
            if (!ScrollVirtualTrackIntoView(target)) return;
            InterpolableDisplay d; if (!_virtualTrackDisplays.TryGetValue(target, out d)) return;
            d.editing = target; d.inputField.text = string.IsNullOrEmpty(target.alias) ? target.name : target.alias;
            d.inputField.gameObject.SetActive(true); d.inputField.ActivateInputField(); d.inputField.Select();
        }
        private void BeginVirtualGroupRename(GroupNode<InterpolableGroup> target)
        {
            int index = _virtualRows.FindIndex(r => r.group == target); if (index < 0) return;
            ScrollVirtualRowIntoView(index);
            HeaderDisplay d = _displayedOwnerHeader.Find(h => h.gameObject.activeSelf && h.group == target); if (d == null) return;
            d.editing = target; d.inputField.text = target.obj.name;
            d.inputField.gameObject.SetActive(true); d.inputField.ActivateInputField(); d.inputField.Select();
        }
        private void VirtualBeginKeyDrag(KeyframeDisplay display, PointerEventData e)
        {
            if (!Input.GetKey(KeyCode.LeftAlt) || display.keyframe == null) return;
            Vector2 local; if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_keyframesContainer, e.position, e.pressEventCamera, out local)) return;
            _virtualDragOffsets.Clear(); _selectedKeyframesXOffset.Clear();
            foreach (var pair in _selectedKeyframes) _virtualDragOffsets[pair.Value] = _baseGridWidth * _zoomLevel * pair.Key / 10f - local.x;
            _virtualDragX = local.x;
            if (!_virtualDragOffsets.TryGetValue(display.keyframe, out _virtualDragAnchorOffset)) _virtualDragAnchorOffset = 0f;
            if (_virtualDragOffsets.Count != 0) isPlaying = false;
            e.Reset();
        }
        private void VirtualDragKeys(PointerEventData e)
        {
            if (_virtualDragOffsets.Count == 0) return;
            Vector2 local; if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_keyframesContainer, e.position, e.pressEventCamera, out local)) return;
            float x = local.x;
            foreach (float offset in _virtualDragOffsets.Values) if (local.x + offset < 0f) x = local.x - (local.x + offset);
            if (Input.GetKey(KeyCode.LeftShift))
            {
                float time = 10f * x / (_baseGridWidth * _zoomLevel), beat = _blockLength / _divisions, mod = time % beat;
                time += mod / beat > 0.5f ? beat - mod : -mod;
                x = time * _baseGridWidth * _zoomLevel / 10f - _virtualDragAnchorOffset;
            }
            _virtualDragX = x;
            foreach (var pair in _virtualKeyDisplays)
            {
                float offset; if (!_virtualDragOffsets.TryGetValue(pair.Key, out offset)) continue;
                RectTransform rt = (RectTransform)pair.Value.gameObject.transform; rt.anchoredPosition = new Vector2(x + offset, rt.anchoredPosition.y);
            }
            e.Reset();
        }
        private void VirtualEndKeyDrag(PointerEventData e)
        {
            if (_virtualDragOffsets.Count == 0) return;
            Dictionary<Keyframe, float> moves = new Dictionary<Keyframe, float>();
            foreach (var pair in _virtualDragOffsets) moves[pair.Key] = 10f * (_virtualDragX + pair.Value) / (_baseGridWidth * _zoomLevel);
            int blocked = 0;
            foreach (var move in moves) { Keyframe occupant = FindOccupant(move.Key.parent, move.Value, move.Key); if (occupant != null && !moves.ContainsKey(occupant)) ++blocked; }
            if (blocked > 0) Logger.LogMessage("Move blocked: " + blocked + " keyframe(s) would overlap existing keyframes");
            else
            {
                List<KeyValuePair<Keyframe, float>> ordered = new List<KeyValuePair<Keyframe, float>>(moves);
                Keyframe reference = ordered[0].Key;
                float oldTime = reference.parent.keyframes.Keys[reference.parent.keyframes.IndexOfValue(reference)];
                bool forward = ordered[0].Value >= oldTime;
                ordered.Sort((a, b) => forward ? b.Value.CompareTo(a.Value) : a.Value.CompareTo(b.Value));
                foreach (var move in ordered) if (!TryMoveKeyframe(move.Key, move.Value)) Logger.LogError("Move to " + move.Value + " failed despite passing the collision precheck");
            }
            e.Reset(); _virtualDragOffsets.Clear(); _selectedKeyframesXOffset.Clear(); UpdateGrid(); UpdateKeyframeWindow(false);
        }
        private bool VirtualPoolsOversized()
        {
            return _displayedKeyframes.Count > UIVirtualRange.PoolTarget(_virtualKeyCount, 64) * 2 || _displayedInterpolables.Count > UIVirtualRange.PoolTarget(_virtualTrackCount, 8) * 2 || _displayedOwnerHeader.Count > UIVirtualRange.PoolTarget(_virtualHeaderCount, 8) * 2 || _displayedInterpolableModels.Count > UIVirtualRange.PoolTarget(_virtualModelCount, 8) * 2 || _timeTexts.Count > UIVirtualRange.PoolTarget(_virtualLabelCount, 8) * 2 || _interpolableSeparators.Count > UIVirtualRange.PoolTarget(_virtualEndRow - _virtualFirstRow, 16) * 2;
        }
        private void TrimVirtualPools(bool closed)
        {
            if (_virtualDragOffsets.Count != 0 && !closed) return;
            if (!closed && (!VirtualPoolsOversized() || Time.unscaledTime - _virtualLowDemandSince < 5f)) return;
            if (closed)
            {
                _virtualDragOffsets.Clear(); _selectedKeyframesXOffset.Clear(); _virtualTrackDisplays.Clear(); _virtualKeyDisplays.Clear();
                _virtualTrackCount = _virtualHeaderCount = _virtualModelCount = _virtualKeyCount = _virtualLabelCount = 0;
                foreach (var d in _displayedInterpolables) { d.editing = null; d.inputField.gameObject.SetActive(false); d.gameObject.SetActive(false); d.gridBackground.gameObject.SetActive(false); d.interpolable = null; }
                foreach (var d in _displayedOwnerHeader) { d.editing = null; d.inputField.gameObject.SetActive(false); d.gameObject.SetActive(false); d.group = null; d.owner = null; }
                foreach (var d in _displayedInterpolableModels) { d.gameObject.SetActive(false); d.model = null; }
                foreach (var d in _displayedKeyframes) { d.gameObject.SetActive(false); d.keyframe = null; d.parent = null; }
                foreach (var d in _timeTexts) d.gameObject.SetActive(false);
                foreach (var d in _interpolableSeparators) d.gameObject.SetActive(false);
                _virtualHoveredKey = null;
                _tooltip.transform.parent.gameObject.SetActive(false);
                _virtualLayoutPending = true;
            }
            TrimPool(_displayedKeyframes, UIVirtualRange.PoolTarget(_virtualKeyCount, 64), d => UnityEngine.Object.Destroy(d.gameObject));
            TrimPool(_displayedInterpolables, UIVirtualRange.PoolTarget(_virtualTrackCount, 8), d => { UnityEngine.Object.Destroy(d.background.material); UnityEngine.Object.Destroy(d.gridBackground.material); UnityEngine.Object.Destroy(d.gridBackground.gameObject); UnityEngine.Object.Destroy(d.gameObject); });
            TrimPool(_displayedOwnerHeader, UIVirtualRange.PoolTarget(_virtualHeaderCount, 8), d => UnityEngine.Object.Destroy(d.gameObject));
            TrimPool(_displayedInterpolableModels, UIVirtualRange.PoolTarget(_virtualModelCount, 8), d => UnityEngine.Object.Destroy(d.gameObject));
            TrimPool(_timeTexts, UIVirtualRange.PoolTarget(_virtualLabelCount, 8), d => UnityEngine.Object.Destroy(d.gameObject));
            TrimPool(_interpolableSeparators, UIVirtualRange.PoolTarget(closed ? 0 : _virtualEndRow - _virtualFirstRow, 16), d => UnityEngine.Object.Destroy(d.gameObject));
            _virtualLowDemandSince = Time.unscaledTime;
        }

#if FAST_TIMELINE_UI_VALIDATE
        private void ValidateVirtualViewport()
        {
            int expectedKeys = 0;
            for (int r = _virtualFirstRow; r < _virtualEndRow; ++r)
            {
                VirtualRow row = _virtualRows[r];
                if (row.leaf == null) continue;
                InterpolableDisplay d;
                if (!_virtualTrackDisplays.TryGetValue(row.leaf.obj, out d) || d.interpolable != row.leaf || ((RectTransform)d.gameObject.transform).anchoredPosition.y != row.Center)
                    Logger.LogError("UI VALIDATE: row binding/position mismatch at " + r);
                var keys = row.leaf.obj.keyframes;
                for (int k = 0; k < keys.Count; ++k)
                    if (keys.Keys[k] >= _virtualTimeStart - UIVirtualRange.Epsilon && keys.Keys[k] <= _virtualTimeEnd + UIVirtualRange.Epsilon) ++expectedKeys;
            }
            if (expectedKeys != _virtualKeyCount) Logger.LogError("UI VALIDATE: expected " + expectedKeys + " keys, materialized " + _virtualKeyCount);
            for (int i = 0; i < _virtualKeyCount; ++i)
            {
                KeyframeDisplay d = _displayedKeyframes[i];
                int index = d.parent.keyframes.IndexOfKey(d.time);
                if (index < 0 || !ReferenceEquals(d.parent.keyframes.Values[index], d.keyframe)) Logger.LogError("UI VALIDATE: stale key binding at " + i);
            }
        }
#endif

        private static void TrimPool<T>(List<T> pool, int target, Action<T> release)
        {
            for (int i = pool.Count - 1; i >= target; --i) { release(pool[i]); pool.RemoveAt(i); }
        }
    }
}

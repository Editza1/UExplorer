using System.Numerics;
using CUE4Parse.UE4.Objects.Engine;
using ImGuiNET;
using UExplorer.Rendering;
using UExplorer.Scene;

namespace UExplorer.UI.Editors;

public sealed class MaterialGraphEditor : IDisposable
{
    private readonly MaterialAsset _material;
    private UeMatGraph _graph;
    private readonly HashSet<UeMatNode> _selectedNodes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(UeMatNode Node, UeMatPin Pin), Vector2> _pinScreenPositions = new();

    private Vector2 _pan = new(420f, 260f);
    private float _zoom = 0.85f;
    private bool _needsInitialFrame = true;
    private bool _showT3dInspector;
    private string _t3dPreviewText = "";
    private string _searchFilter = "";
    private string _statusMessage = "";
    private double _statusTimestamp;

    private UeMatNode? _draggedNode;
    private UeMatComment? _draggedComment;
    private readonly List<UeMatNode> _commentEnclosedNodes = [];
    private Vector2 _dragLastMouse;
    private bool _isBoxSelecting;
    private Vector2 _boxSelectStart;

    public MaterialGraphEditor(MaterialAsset material)
    {
        _material = material;
        _graph = UeMaterialGraphBuilder.Build(material);
    }

    public UeMatNode? SelectedNode => _selectedNodes.FirstOrDefault();

    public void RebuildFromShader()
    {
        _graph = UeMaterialGraphBuilder.Build(_material);
        _selectedNodes.Clear();
        _needsInitialFrame = true;
        if (_showT3dInspector)
            _t3dPreviewText = UeMaterialGraphBuilder.ExportToUeT3D(_graph);
        SetStatus($"Reconstructed {_graph.Nodes.Count + 1} UE Material Graph nodes ({_graph.Comments.Count} function comment groups).");
    }

    public void CopyAllToClipboard()
    {
        var t3d = UeMaterialGraphBuilder.ExportToUeT3D(_graph);
        ImGui.SetClipboardText(t3d);
        _t3dPreviewText = t3d;
        SetStatus($"Copied all {_graph.Nodes.Count + 1} UE Material Graph nodes + {_graph.Comments.Count} comment boxes to clipboard (Ctrl+V in UE5)!");
    }

    public void CopySelectedToClipboard()
    {
        if (_selectedNodes.Count == 0)
        {
            CopyAllToClipboard();
            return;
        }
        var t3d = UeMaterialGraphBuilder.ExportToUeT3D(_graph, _selectedNodes);
        ImGui.SetClipboardText(t3d);
        _t3dPreviewText = t3d;
        SetStatus($"Copied {_selectedNodes.Count} selected UE Material Graph node(s) to clipboard (Ctrl+V in UE5)!");
    }

    public void PasteFromClipboard()
    {
        string? clip = null;
        try { clip = ImGui.GetClipboardText(); } catch { }
        if (string.IsNullOrWhiteSpace(clip))
        {
            SetStatus("Clipboard is empty.");
            return;
        }
        if (UeMaterialGraphBuilder.TryImportFromUeT3D(clip, out var imported) && imported is not null)
        {
            _graph = imported;
            _selectedNodes.Clear();
            _needsInitialFrame = true;
            _t3dPreviewText = clip;
            SetStatus($"Pasted {_graph.Nodes.Count + 1} UE Material Graph nodes from T3D clipboard!");
        }
        else
        {
            SetStatus("Clipboard does not contain valid UE MaterialGraphNode T3D text.");
        }
    }

    private void SetStatus(string msg)
    {
        _statusMessage = msg;
        _statusTimestamp = ImGui.GetTime();
    }

    public void Draw(Ue52AssetPreviewWindow.PreviewTab tab)
    {
        DrawToolbar();

        var avail = ImGui.GetContentRegionAvail();
        if (avail.X < 32f || avail.Y < 32f)
            return;

        if (_showT3dInspector)
        {
            var canvasW = MathF.Max(220f, avail.X * 0.60f);
            var inspectorW = MathF.Max(180f, avail.X - canvasW - 6f);

            ImGui.BeginChild("##MatGraphCanvasChild", new Vector2(canvasW, avail.Y), ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
            DrawGraphCanvas(tab, ImGui.GetContentRegionAvail());
            ImGui.EndChild();

            ImGui.SameLine();

            ImGui.BeginChild("##MatGraphT3dChild", new Vector2(inspectorW, avail.Y), ImGuiChildFlags.Borders);
            DrawT3dInspector();
            ImGui.EndChild();
        }
        else
        {
            ImGui.BeginChild("##MatGraphCanvasChild", avail, ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
            DrawGraphCanvas(tab, ImGui.GetContentRegionAvail());
            ImGui.EndChild();
        }
    }

    private void DrawToolbar()
    {
        if (ImGui.Button("Copy All UE Nodes (T3D)"))
            CopyAllToClipboard();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Copies the entire Material Graph in Unreal Engine 5 T3D format (with Engine fallback textures & Function comments) ready to Ctrl+V in UE5.");

        ImGui.SameLine();
        ImGui.BeginDisabled(_selectedNodes.Count == 0);
        if (ImGui.Button($"Copy Selected ({_selectedNodes.Count})"))
            CopySelectedToClipboard();
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Paste UE Nodes (T3D)"))
            PasteFromClipboard();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Pastes Unreal Engine 5 T3D MaterialGraphNode text from the clipboard into this graph.");

        ImGui.SameLine();
        if (ImGui.Button("Frame All"))
            _needsInitialFrame = true;

        ImGui.SameLine();
        if (ImGui.Button("Rebuild"))
            RebuildFromShader();

        ImGui.SameLine();
        if (ImGui.Checkbox("T3D Code", ref _showT3dInspector) && _showT3dInspector && string.IsNullOrEmpty(_t3dPreviewText))
            _t3dPreviewText = UeMaterialGraphBuilder.ExportToUeT3D(_graph);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(150f);
        ImGui.InputTextWithHint("##MatGraphSearch", "Filter nodes...", ref _searchFilter, 128);

        ImGui.SameLine();
        ImGui.TextDisabled($"Nodes: {_graph.Nodes.Count + 1} | Functions/Groups: {_graph.Comments.Count} | Zoom: {_zoom * 100f:0}%");

        if (!string.IsNullOrEmpty(_statusMessage) && ImGui.GetTime() - _statusTimestamp < 6.0)
        {
            ImGui.TextColored(new Vector4(0.35f, 0.92f, 0.52f, 1f), _statusMessage);
        }
    }

    private void DrawT3dInspector()
    {
        ImGui.TextColored(new Vector4(0.93f, 0.65f, 0.14f, 1f), "UE5 T3D Clipboard Text");
        ImGui.SameLine();
        if (ImGui.SmallButton("Refresh All"))
            _t3dPreviewText = UeMaterialGraphBuilder.ExportToUeT3D(_graph);
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy Text"))
        {
            ImGui.SetClipboardText(_t3dPreviewText);
            SetStatus("Copied T3D text from inspector to clipboard.");
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Apply Paste"))
        {
            if (UeMaterialGraphBuilder.TryImportFromUeT3D(_t3dPreviewText, out var parsed) && parsed is not null)
            {
                _graph = parsed;
                _needsInitialFrame = true;
                SetStatus($"Imported {_graph.Nodes.Count + 1} UE nodes from T3D editor.");
            }
        }
        ImGui.Separator();

        var avail = ImGui.GetContentRegionAvail();
        ImGui.InputTextMultiline("##T3dRawText", ref _t3dPreviewText, 256 * 1024, avail, ImGuiInputTextFlags.AllowTabInput);
    }

    private void DrawGraphCanvas(Ue52AssetPreviewWindow.PreviewTab tab, Vector2 canvasSize)
    {
        var drawList = ImGui.GetWindowDrawList();
        var canvasPos = ImGui.GetCursorScreenPos();
        var winMin = ImGui.GetWindowPos();
        var winMax = winMin + ImGui.GetWindowSize();
        var clipMin = Vector2.Max(canvasPos, winMin);
        var clipMax = Vector2.Min(canvasPos + canvasSize, winMax);
        if (clipMax.X <= clipMin.X + 4f || clipMax.Y <= clipMin.Y + 4f)
            return;

        var canvasMax = clipMax;

        // Calculate node sizes first
        ComputeNodeSizes();

        if (_needsInitialFrame && canvasSize.X > 64f && canvasSize.Y > 64f)
        {
            FrameAllNodes(canvasSize);
            _needsInitialFrame = false;
        }

        ImGui.InvisibleButton("##MatGraphCanvasBtn", canvasSize, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        var canvasHovered = ImGui.IsItemHovered();
        var io = ImGui.GetIO();
        var mousePos = io.MousePos;

        // Keyboard shortcuts when canvas is hovered/focused
        if (canvasHovered && !io.WantTextInput)
        {
            if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.C, repeat: false))
                CopySelectedToClipboard();
            if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.V, repeat: false))
                PasteFromClipboard();
            if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.A, repeat: false))
            {
                _selectedNodes.Clear();
                _selectedNodes.Add(_graph.RootNode);
                foreach (var n in _graph.Nodes) _selectedNodes.Add(n);
            }
            if (ImGui.IsKeyPressed(ImGuiKey.F, repeat: false))
                FrameAllNodes(canvasSize);
        }

        // Zoom with mouse wheel around cursor (clamped to safe range)
        if (canvasHovered && MathF.Abs(io.MouseWheel) > 0.001f)
        {
            var oldZoom = _zoom;
            _zoom = Math.Clamp(_zoom * MathF.Pow(1.12f, io.MouseWheel), 0.20f, 1.50f);
            var mouseRel = mousePos - canvasPos;
            _pan = mouseRel - (mouseRel - _pan) * (_zoom / oldZoom);
        }

        // Pan with Right or Middle mouse drag
        if (canvasHovered && (ImGui.IsMouseDragging(ImGuiMouseButton.Right, 2f) || ImGui.IsMouseDragging(ImGuiMouseButton.Middle, 2f)))
        {
            _pan += io.MouseDelta;
        }

        // Clamp pan so coordinates never become NaN/Infinity
        if (!float.IsFinite(_pan.X) || !float.IsFinite(_pan.Y))
            _pan = new Vector2(420f, 260f);
        _pan.X = Math.Clamp(_pan.X, -200000f, 200000f);
        _pan.Y = Math.Clamp(_pan.Y, -200000f, 200000f);

        Vector2 GraphToScreen(Vector2 gPos) => canvasPos + _pan + gPos * _zoom;
        Vector2 ScreenToGraph(Vector2 sPos) => (sPos - canvasPos - _pan) / _zoom;
        bool IntersectsCanvas(Vector2 min, Vector2 max, float pad = 32f) =>
            max.X >= clipMin.X - pad && min.X <= clipMax.X + pad &&
            max.Y >= clipMin.Y - pad && min.Y <= clipMax.Y + pad;

        drawList.PushClipRect(clipMin, clipMax, true);
        try
        {
            // 1. Background & UE5 Grid (strictly inside clip rect)
            drawList.AddRectFilled(clipMin, clipMax, ImGui.GetColorU32(new Vector4(0.085f, 0.090f, 0.100f, 1f)));
            DrawGrid(drawList, canvasPos, canvasSize);

            // 2. Draw MaterialGraphNode_Comment boxes (behind nodes)
            foreach (var comment in _graph.Comments)
            {
                var cMin = GraphToScreen(new Vector2(comment.PosX, comment.PosY));
                var cMax = GraphToScreen(new Vector2(comment.PosX + comment.SizeX, comment.PosY + comment.SizeY));
                if (!IntersectsCanvas(cMin, cMax))
                    continue;

                var headerH = MathF.Max(20f, 28f * _zoom);
                var rounding = Math.Clamp(6f * _zoom, 2f, 8f);

                var fillCol = ImGui.GetColorU32(new Vector4(comment.Color.X, comment.Color.Y, comment.Color.Z, 0.18f));
                var headerCol = ImGui.GetColorU32(new Vector4(comment.Color.X * 1.15f, comment.Color.Y * 1.15f, comment.Color.Z * 1.15f, 0.72f));
                var borderCol = ImGui.GetColorU32(new Vector4(comment.Color.X * 1.4f, comment.Color.Y * 1.4f, comment.Color.Z * 1.4f, 0.85f));

                drawList.AddRectFilled(cMin, cMax, fillCol, rounding);
                drawList.AddRectFilled(cMin, new Vector2(cMax.X, MathF.Min(cMax.Y, cMin.Y + headerH)), headerCol, rounding, ImDrawFlags.RoundCornersTop);
                drawList.AddRect(cMin, cMax, borderCol, rounding, ImDrawFlags.None, 1.5f);

                if (_zoom > 0.30f)
                {
                    drawList.AddText(cMin + new Vector2(10f * _zoom, 5f * _zoom), ImGui.GetColorU32(new Vector4(0.95f, 0.96f, 0.98f, 1f)), comment.Text);
                }
            }

            // 3. Record Pin Screen Positions for all nodes + RootNode
            _pinScreenPositions.Clear();
            var allNodes = new List<UeMatNode>(_graph.Nodes.Count + 1);
            allNodes.AddRange(_graph.Nodes);
            allNodes.Add(_graph.RootNode);

            foreach (var node in allNodes)
            {
                var nPos = GraphToScreen(new Vector2(node.PosX, node.PosY));
                var nSize = node.CachedSize * _zoom;
                var headerH = 26f * _zoom;
                var pinStartY = nPos.Y + headerH + 12f * _zoom;
                var pinStepY = 20f * _zoom;

                var visibleInputs = node.InputPins.Where(p => !p.IsAdvancedView).ToList();
                for (var i = 0; i < visibleInputs.Count; i++)
                {
                    var pPos = new Vector2(nPos.X + 10f * _zoom, pinStartY + i * pinStepY);
                    _pinScreenPositions[(node, visibleInputs[i])] = pPos;
                }
                for (var i = 0; i < node.OutputPins.Count; i++)
                {
                    var pPos = new Vector2(nPos.X + nSize.X - 10f * _zoom, pinStartY + i * pinStepY);
                    _pinScreenPositions[(node, node.OutputPins[i])] = pPos;
                }
            }

            // 4. Draw Cubic Bezier Connection Wires (viewport-culled + fixed 24 segments)
            foreach (var node in allNodes)
            {
                foreach (var outPin in node.OutputPins)
                {
                    if (!_pinScreenPositions.TryGetValue((node, outPin), out var p1))
                        continue;
                    var wireColor = GetPinColor(outPin, isWire: true);
                    foreach (var (targetNode, targetPin) in outPin.LinkedTo)
                    {
                        if (!_pinScreenPositions.TryGetValue((targetNode, targetPin), out var p2))
                            continue;
                        var wMin = Vector2.Min(p1, p2);
                        var wMax = Vector2.Max(p1, p2);
                        if (!IntersectsCanvas(wMin, wMax, 80f))
                            continue;

                        var isHighlighted = _selectedNodes.Contains(node) || _selectedNodes.Contains(targetNode);
                        var col = isHighlighted
                            ? ImGui.GetColorU32(new Vector4(0.98f, 0.68f, 0.18f, 1f))
                            : wireColor;
                        var thickness = (isHighlighted ? 2.6f : 1.8f) * Math.Clamp(_zoom, 0.5f, 1.25f);
                        var dx = Math.Clamp(MathF.Abs(p2.X - p1.X) * 0.45f, 36f * _zoom, 240f * _zoom);
                        drawList.AddBezierCubic(p1, p1 + new Vector2(dx, 0f), p2 - new Vector2(dx, 0f), p2, col, thickness, 24);
                    }
                }
            }

            // 5. Handle Mouse Picking & Dragging
            if (canvasHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                UeMatNode? clickedNode = null;
                for (var i = allNodes.Count - 1; i >= 0; i--)
                {
                    var n = allNodes[i];
                    var nMin = GraphToScreen(new Vector2(n.PosX, n.PosY));
                    var nMax = nMin + n.CachedSize * _zoom;
                    if (mousePos.X >= nMin.X && mousePos.X <= nMax.X && mousePos.Y >= nMin.Y && mousePos.Y <= nMax.Y)
                    {
                        clickedNode = n;
                        break;
                    }
                }

                if (clickedNode is not null)
                {
                    if (io.KeyCtrl)
                    {
                        if (!_selectedNodes.Remove(clickedNode))
                            _selectedNodes.Add(clickedNode);
                    }
                    else if (!_selectedNodes.Contains(clickedNode))
                    {
                        _selectedNodes.Clear();
                        _selectedNodes.Add(clickedNode);
                    }
                    _draggedNode = clickedNode;
                    _dragLastMouse = mousePos;
                    tab.SelectedExpression = clickedNode.SourceExpression;
                }
                else
                {
                    UeMatComment? clickedComment = null;
                    for (var i = _graph.Comments.Count - 1; i >= 0; i--)
                    {
                        var c = _graph.Comments[i];
                        var cMin = GraphToScreen(new Vector2(c.PosX, c.PosY));
                        var cHeaderMax = new Vector2(cMin.X + c.SizeX * _zoom, cMin.Y + MathF.Max(20f, 28f * _zoom));
                        if (mousePos.X >= cMin.X && mousePos.X <= cHeaderMax.X && mousePos.Y >= cMin.Y && mousePos.Y <= cHeaderMax.Y)
                        {
                            clickedComment = c;
                            break;
                        }
                    }

                    if (clickedComment is not null)
                    {
                        _draggedComment = clickedComment;
                        _dragLastMouse = mousePos;
                        _commentEnclosedNodes.Clear();
                        foreach (var n in _graph.Nodes)
                        {
                            if (n.PosX >= clickedComment.PosX && n.PosX <= clickedComment.PosX + clickedComment.SizeX &&
                                n.PosY >= clickedComment.PosY && n.PosY <= clickedComment.PosY + clickedComment.SizeY)
                            {
                                _commentEnclosedNodes.Add(n);
                            }
                        }
                    }
                    else
                    {
                        if (!io.KeyCtrl)
                            _selectedNodes.Clear();
                        _isBoxSelecting = true;
                        _boxSelectStart = mousePos;
                        tab.SelectedExpression = null;
                    }
                }
            }

            if (_draggedNode is not null)
            {
                if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                {
                    var deltaGraph = (mousePos - _dragLastMouse) / _zoom;
                    if (deltaGraph.LengthSquared() >= 1f)
                    {
                        var dx = (int) MathF.Round(deltaGraph.X);
                        var dy = (int) MathF.Round(deltaGraph.Y);
                        foreach (var sel in _selectedNodes)
                        {
                            sel.PosX += dx;
                            sel.PosY += dy;
                        }
                        _dragLastMouse += new Vector2(dx, dy) * _zoom;
                    }
                }
                else
                {
                    _draggedNode = null;
                }
            }
            else if (_draggedComment is not null)
            {
                if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                {
                    var deltaGraph = (mousePos - _dragLastMouse) / _zoom;
                    if (deltaGraph.LengthSquared() >= 1f)
                    {
                        var dx = (int) MathF.Round(deltaGraph.X);
                        var dy = (int) MathF.Round(deltaGraph.Y);
                        _draggedComment.PosX += dx;
                        _draggedComment.PosY += dy;
                        foreach (var enc in _commentEnclosedNodes)
                        {
                            enc.PosX += dx;
                            enc.PosY += dy;
                        }
                        _dragLastMouse += new Vector2(dx, dy) * _zoom;
                    }
                }
                else
                {
                    _draggedComment = null;
                    _commentEnclosedNodes.Clear();
                }
            }
            else if (_isBoxSelecting)
            {
                var bMin = Vector2.Min(_boxSelectStart, mousePos);
                var bMax = Vector2.Max(_boxSelectStart, mousePos);
                drawList.AddRectFilled(bMin, bMax, ImGui.GetColorU32(new Vector4(0.95f, 0.65f, 0.14f, 0.15f)));
                drawList.AddRect(bMin, bMax, ImGui.GetColorU32(new Vector4(0.95f, 0.65f, 0.14f, 0.85f)), 0f, ImDrawFlags.None, 1.5f);

                if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
                {
                    var gMin = ScreenToGraph(bMin);
                    var gMax = ScreenToGraph(bMax);
                    foreach (var n in allNodes)
                    {
                        var nCenter = new Vector2(n.PosX, n.PosY) + n.CachedSize * 0.5f;
                        if (nCenter.X >= gMin.X && nCenter.X <= gMax.X && nCenter.Y >= gMin.Y && nCenter.Y <= gMax.Y)
                            _selectedNodes.Add(n);
                    }
                    _isBoxSelecting = false;
                }
            }

            // 6. Draw Nodes (viewport-culled)
            var device = D3D11NativeShaderDevice.Shared;
            foreach (var node in allNodes)
            {
                var nMin = GraphToScreen(new Vector2(node.PosX, node.PosY));
                var nMax = nMin + node.CachedSize * _zoom;
                if (!IntersectsCanvas(nMin, nMax))
                    continue;
                DrawSingleNode(drawList, node, nMin, device);
            }
        }
        finally
        {
            drawList.PopClipRect();
        }

        // 7. Right-Click Context Menu
        if (canvasHovered && ImGui.IsMouseReleased(ImGuiMouseButton.Right) && io.MouseDragMaxDistanceSqr[1] < 25f)
            ImGui.OpenPopup("##MatGraphContextPopup");

        if (ImGui.BeginPopup("##MatGraphContextPopup"))
        {
            if (ImGui.MenuItem("Copy All UE Nodes (T3D)", "Ctrl+C"))
                CopyAllToClipboard();
            if (ImGui.MenuItem($"Copy Selected Nodes ({_selectedNodes.Count}) (T3D)", null, false, _selectedNodes.Count > 0))
                CopySelectedToClipboard();
            if (ImGui.MenuItem("Paste UE Nodes (T3D)", "Ctrl+V"))
                PasteFromClipboard();
            ImGui.Separator();
            if (ImGui.MenuItem("Select All Nodes", "Ctrl+A"))
            {
                _selectedNodes.Clear();
                _selectedNodes.Add(_graph.RootNode);
                foreach (var n in _graph.Nodes) _selectedNodes.Add(n);
            }
            if (ImGui.MenuItem("Frame All Nodes", "F"))
                FrameAllNodes(canvasSize);
            if (ImGui.MenuItem("Rebuild Graph from Shader"))
                RebuildFromShader();
            ImGui.EndPopup();
        }
    }

    private void DrawSingleNode(ImDrawListPtr drawList, UeMatNode node, Vector2 nPos, D3D11NativeShaderDevice? device)
    {
        var nSize = node.CachedSize * _zoom;
        var nMax = nPos + nSize;
        var isSelected = _selectedNodes.Contains(node);
        var matchesSearch = !string.IsNullOrWhiteSpace(_searchFilter) &&
                            (node.Title.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase) ||
                             node.Subtitle.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase));

        var rounding = Math.Clamp(6f * _zoom, 2f, 8f);
        var headerH = 26f * _zoom;

        // Shadow
        drawList.AddRectFilled(nPos + new Vector2(3f, 4f) * _zoom, nMax + new Vector2(3f, 4f) * _zoom, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.45f)), rounding);

        // Body & Header
        drawList.AddRectFilled(nPos, nMax, ImGui.GetColorU32(new Vector4(0.14f, 0.15f, 0.17f, 0.96f)), rounding);
        drawList.AddRectFilled(nPos, new Vector2(nMax.X, MathF.Min(nMax.Y, nPos.Y + headerH)), ImGui.GetColorU32(node.HeaderColor), rounding, ImDrawFlags.RoundCornersTop);

        // Border (UE5 Orange when selected, Cyan when search-matched)
        var borderCol = isSelected
            ? ImGui.GetColorU32(new Vector4(0.96f, 0.65f, 0.14f, 1f))
            : (matchesSearch
                ? ImGui.GetColorU32(new Vector4(0.25f, 0.85f, 1.0f, 1f))
                : ImGui.GetColorU32(new Vector4(0.05f, 0.05f, 0.06f, 1f)));
        var borderThick = (isSelected || matchesSearch) ? 2.4f : 1.2f;
        drawList.AddRect(nPos, nMax, borderCol, rounding, ImDrawFlags.None, borderThick);

        if (_zoom > 0.35f)
        {
            drawList.AddText(nPos + new Vector2(8f * _zoom, 5f * _zoom), ImGui.GetColorU32(new Vector4(0.96f, 0.97f, 0.99f, 1f)), node.Title);
        }

        // Input Pins
        var visibleInputs = node.InputPins.Where(p => !p.IsAdvancedView).ToList();
        foreach (var inPin in visibleInputs)
        {
            if (!_pinScreenPositions.TryGetValue((node, inPin), out var pPos))
                continue;
            var pCol = GetPinColor(inPin, isWire: false);
            var r = Math.Clamp(4.5f * _zoom, 2f, 6.5f);
            if (inPin.LinkedTo.Count > 0)
                drawList.AddCircleFilled(pPos, r, pCol, 8);
            else
                drawList.AddCircle(pPos, r, pCol, 8, 1.5f);

            if (_zoom > 0.42f)
                drawList.AddText(pPos + new Vector2(8f * _zoom, -7f * _zoom), ImGui.GetColorU32(new Vector4(0.84f, 0.86f, 0.90f, 1f)), inPin.PinName);
        }

        // Output Pins
        foreach (var outPin in node.OutputPins)
        {
            if (!_pinScreenPositions.TryGetValue((node, outPin), out var pPos))
                continue;
            var pCol = GetPinColor(outPin, isWire: false);
            var r = Math.Clamp(4.5f * _zoom, 2f, 6.5f);
            if (outPin.LinkedTo.Count > 0)
                drawList.AddCircleFilled(pPos, r, pCol, 8);
            else
                drawList.AddCircle(pPos, r, pCol, 8, 1.5f);

            if (_zoom > 0.42f && !string.Equals(outPin.PinName, "Output", StringComparison.OrdinalIgnoreCase))
            {
                var textSize = ImGui.CalcTextSize(outPin.PinName);
                drawList.AddText(new Vector2(pPos.X - textSize.X - 7f * _zoom, pPos.Y - 7f * _zoom), ImGui.GetColorU32(new Vector4(0.84f, 0.86f, 0.90f, 1f)), outPin.PinName);
            }
        }

        // Preview Texture or Color Swatch at bottom of node
        if (_zoom > 0.40f)
        {
            if (node.PreviewTexture is { } tex && device is not null)
            {
                Vortice.Direct3D11.ID3D11ShaderResourceView? srv = null;
                try { srv = NativeTextureCache.Get(device.Device, tex); } catch { }
                if (srv is not null && srv.NativePointer != IntPtr.Zero)
                {
                    var thumbSz = Math.Clamp(52f * _zoom, 16f, 78f);
                    var tMin = new Vector2(nPos.X + 12f * _zoom, nMax.Y - thumbSz - 8f * _zoom);
                    var tMax = tMin + new Vector2(thumbSz, thumbSz);
                    drawList.AddImage(srv.NativePointer, tMin, tMax);
                    drawList.AddRect(tMin, tMax, ImGui.GetColorU32(new Vector4(0.3f, 0.3f, 0.35f, 1f)));
                }
            }
            else if (node.PreviewColor is { } col)
            {
                var swH = 18f * _zoom;
                var sMin = new Vector2(nPos.X + 12f * _zoom, nMax.Y - swH - 8f * _zoom);
                var sMax = new Vector2(nMax.X - 12f * _zoom, nMax.Y - 8f * _zoom);
                drawList.AddRectFilled(sMin, sMax, ImGui.GetColorU32(new Vector4(col.X, col.Y, col.Z, 1f)), 3f * _zoom);
                drawList.AddRect(sMin, sMax, ImGui.GetColorU32(new Vector4(0.3f, 0.3f, 0.35f, 1f)), 3f * _zoom);
            }
            else if (!string.IsNullOrEmpty(node.Subtitle))
            {
                drawList.AddText(new Vector2(nPos.X + 12f * _zoom, nMax.Y - 20f * _zoom), ImGui.GetColorU32(new Vector4(0.65f, 0.70f, 0.76f, 1f)), node.Subtitle);
            }
        }
    }

    private void ComputeNodeSizes()
    {
        void SizeOne(UeMatNode n)
        {
            var visIn = n.InputPins.Count(p => !p.IsAdvancedView);
            var rows = Math.Max(visIn, n.OutputPins.Count);
            var extraH = (n.PreviewTexture is not null ? 62f : (n.PreviewColor is not null ? 28f : (!string.IsNullOrEmpty(n.Subtitle) ? 20f : 0f)));
            var w = n.IsRoot ? 235f : Math.Clamp(Math.Max(n.Title.Length * 7.8f + 36f, 195f), 195f, 290f);
            var h = Math.Max(76f, 38f + rows * 20f + extraH);
            n.CachedSize = new Vector2(w, h);
        }

        SizeOne(_graph.RootNode);
        foreach (var n in _graph.Nodes)
            SizeOne(n);
    }

    private void FrameAllNodes(Vector2 canvasSize)
    {
        var min = new Vector2(_graph.RootNode.PosX, _graph.RootNode.PosY);
        var max = min + _graph.RootNode.CachedSize;
        foreach (var n in _graph.Nodes)
        {
            var p = new Vector2(n.PosX, n.PosY);
            min = Vector2.Min(min, p);
            max = Vector2.Max(max, p + n.CachedSize);
        }
        foreach (var c in _graph.Comments)
        {
            var p = new Vector2(c.PosX, c.PosY);
            min = Vector2.Min(min, p);
            max = Vector2.Max(max, p + new Vector2(c.SizeX, c.SizeY));
        }

        var graphSize = Vector2.Max(max - min, new Vector2(400f, 300f));
        var margin = new Vector2(80f, 80f);
        var avail = Vector2.Max(canvasSize - margin, new Vector2(200f, 200f));
        _zoom = Math.Clamp(MathF.Min(avail.X / graphSize.X, avail.Y / graphSize.Y), 0.28f, 1.05f);
        var graphCenter = (min + max) * 0.5f;
        _pan = canvasSize * 0.5f - graphCenter * _zoom;
    }

    private void DrawGrid(ImDrawListPtr drawList, Vector2 canvasPos, Vector2 canvasSize)
    {
        var step = 32f * _zoom;
        if (step < 8f) step *= 4f;
        var minorCol = ImGui.GetColorU32(new Vector4(0.14f, 0.15f, 0.17f, 0.7f));
        var majorCol = ImGui.GetColorU32(new Vector4(0.20f, 0.22f, 0.25f, 0.9f));

        var startX = _pan.X % step;
        if (startX < 0) startX += step;
        for (var x = startX; x < canvasSize.X; x += step)
        {
            var gridIdx = (int) MathF.Round((x - _pan.X) / step);
            var isMajor = (gridIdx % 4) == 0;
            drawList.AddLine(new Vector2(canvasPos.X + x, canvasPos.Y), new Vector2(canvasPos.X + x, canvasPos.Y + canvasSize.Y), isMajor ? majorCol : minorCol);
        }

        var startY = _pan.Y % step;
        if (startY < 0) startY += step;
        for (var y = startY; y < canvasSize.Y; y += step)
        {
            var gridIdx = (int) MathF.Round((y - _pan.Y) / step);
            var isMajor = (gridIdx % 4) == 0;
            drawList.AddLine(new Vector2(canvasPos.X, canvasPos.Y + y), new Vector2(canvasPos.X + canvasSize.X, canvasPos.Y + y), isMajor ? majorCol : minorCol);
        }
    }

    private static uint GetPinColor(UeMatPin pin, bool isWire)
    {
        var a = isWire ? 0.88f : 1.0f;
        return (pin.PinSubCategory.ToLowerInvariant(), pin.PinName.ToUpperInvariant()) switch
        {
            ("red", _) or (_, "R") => ImGui.GetColorU32(new Vector4(0.92f, 0.24f, 0.24f, a)),
            ("green", _) or (_, "G") => ImGui.GetColorU32(new Vector4(0.24f, 0.88f, 0.32f, a)),
            ("blue", _) or (_, "B") => ImGui.GetColorU32(new Vector4(0.26f, 0.52f, 0.96f, a)),
            ("alpha", _) or (_, "A") => ImGui.GetColorU32(new Vector4(0.75f, 0.75f, 0.78f, a)),
            ("rgba", _) or (_, "RGBA") or (_, "RGB") => ImGui.GetColorU32(new Vector4(0.92f, 0.92f, 0.95f, a)),
            _ => ImGui.GetColorU32(new Vector4(0.82f, 0.84f, 0.88f, a))
        };
    }

    public void Dispose()
    {
    }
}

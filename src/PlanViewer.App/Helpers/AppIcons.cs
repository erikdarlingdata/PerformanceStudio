using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Shapes = Avalonia.Controls.Shapes;

namespace PlanViewer.App.Helpers;

/*  Icon path data below is Microsoft's Fluent UI System Icons, taken from the 16px regular
    variants at https://github.com/microsoft/fluentui-system-icons and used under its license:

    MIT License

    Copyright (c) 2020 Microsoft Corporation

    Permission is hereby granted, free of charge, to any person obtaining a copy of this software
    and associated documentation files (the "Software"), to deal in the Software without
    restriction, including without limitation the rights to use, copy, modify, merge, publish,
    distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the
    Software is furnished to do so, subject to the following conditions:

    The above copyright notice and this permission notice shall be included in all copies or
    substantial portions of the Software.

    THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING
    BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
    NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
    DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
    OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.  */

/// <summary>
/// One icon per concept the toolbars need, and the one way to turn one into a control.
///
/// <para><b>Why.</b> The toolbars used to speak three icon languages at once: color emoji
/// (a person, a robot, a clipboard, a bar chart, a memo), geometric text glyphs borrowed from
/// whatever font had them, and nothing at all. Emoji are the worst of those, because the system
/// renders them from its color emoji font: they ignore <c>Foreground</c>, so they stay
/// full-color on a monochrome dark theme and do not follow a button's hover state. These are
/// plain fills that take the ambient foreground exactly like the label beside them.</para>
///
/// <para>Every icon is a single monochrome path with no strokes, all from the same Fluent 16px
/// regular set, so they carry the same weight as each other at the same size.</para>
/// </summary>
public static class AppIcons
{
    /// <summary>
    /// The design grid the paths are drawn on: Fluent's 16px icons are authored inside a 16x16
    /// box, and deliberately do not all fill it — a text-align glyph is wide and short, a bot is
    /// tall and narrow.
    /// </summary>
    private const double IconFrame = 16;

    /// <summary>Rendered size of a toolbar icon, in device-independent pixels.</summary>
    private const double IconSize = 14;

    /// <summary>Advice written for a person to read.</summary>
    /// <remarks>Fluent UI System Icons <c>ic_fluent_person_16_regular</c>.</remarks>
    public static StreamGeometry HumanAdvice { get; } = StreamGeometry.Parse(
        "M11.5 8C12.3284 8 13 8.67157 13 9.5V10C13 11.9714 11.1405 14 8 14C4.85951 14 3 11.9714 3 10V9.5C3 8.67157 3.67157 8 4.5 8H11.5ZM11.5 9H4.5C4.22386 9 4 9.22386 4 9.5V10C4 11.4376 5.43216 13 8 13C10.5678 13 12 11.4376 12 10V9.5C12 9.22386 11.7761 9 11.5 9ZM8 1.5C9.51878 1.5 10.75 2.73122 10.75 4.25C10.75 5.76878 9.51878 7 8 7C6.48122 7 5.25 5.76878 5.25 4.25C5.25 2.73122 6.48122 1.5 8 1.5ZM8 2.5C7.0335 2.5 6.25 3.2835 6.25 4.25C6.25 5.2165 7.0335 6 8 6C8.9665 6 9.75 5.2165 9.75 4.25C9.75 3.2835 8.9665 2.5 8 2.5Z");

    /// <summary>Advice serialized for a model to read.</summary>
    /// <remarks>Fluent UI System Icons <c>ic_fluent_bot_16_regular</c>.</remarks>
    public static StreamGeometry RobotAdvice { get; } = StreamGeometry.Parse(
        "M8.5 1.5C8.5 1.22386 8.27614 1 8 1C7.72386 1 7.5 1.22386 7.5 1.5V2H5.5C4.67157 2 4 2.67157 4 3.5V6.5C4 7.32843 4.67157 8 5.5 8H10.5C11.3284 8 12 7.32843 12 6.5V3.5C12 2.67157 11.3284 2 10.5 2H8.5V1.5ZM5 3.5C5 3.22386 5.22386 3 5.5 3H10.5C10.7761 3 11 3.22386 11 3.5V6.5C11 6.77614 10.7761 7 10.5 7H5.5C5.22386 7 5 6.77614 5 6.5V3.5ZM4 11C4 10.7239 4.22386 10.5 4.5 10.5H11.5C11.7761 10.5 12 10.7239 12 11V11.35C12 12.2965 11.5941 12.9263 10.9231 13.3444C10.2217 13.7814 9.2028 14 8 14C6.80504 14 5.78568 13.7816 5.082 13.3441C4.4083 12.9253 4 12.2951 4 11.35V11ZM4.5 9.5C3.67157 9.5 3 10.1716 3 11V11.35C3 12.6549 3.59906 13.5997 4.55404 14.1934C5.47904 14.7684 6.70968 15 8 15C9.2972 15 10.5283 14.7686 11.4519 14.1931C12.4059 13.5987 13 12.6535 13 11.35V11C13 10.1716 12.3284 9.5 11.5 9.5H4.5ZM7.25 5C7.25 5.41421 6.91421 5.75 6.5 5.75C6.08579 5.75 5.75 5.41421 5.75 5C5.75 4.58579 6.08579 4.25 6.5 4.25C6.91421 4.25 7.25 4.58579 7.25 5ZM9.5 5.75C9.91421 5.75 10.25 5.41421 10.25 5C10.25 4.58579 9.91421 4.25 9.5 4.25C9.08579 4.25 8.75 4.58579 8.75 5C8.75 5.41421 9.08579 5.75 9.5 5.75Z");

    /// <summary>Compare two plans against each other.</summary>
    /// <remarks>Fluent UI System Icons <c>ic_fluent_arrow_swap_16_regular</c>.</remarks>
    public static StreamGeometry Compare { get; } = StreamGeometry.Parse(
        "M10.3536 1.64645C10.1583 1.45118 9.84171 1.45118 9.64645 1.64645C9.45118 1.84171 9.45118 2.15829 9.64645 2.35355L11.2929 4H3.5C3.22386 4 3 4.22386 3 4.5C3 4.77614 3.22386 5 3.5 5H11.2929L9.64645 6.64645C9.45118 6.84171 9.45118 7.15829 9.64645 7.35355C9.84171 7.54882 10.1583 7.54882 10.3536 7.35355L12.8536 4.85355C13.0488 4.65829 13.0488 4.34171 12.8536 4.14645L10.3536 1.64645ZM6.35355 9.35355C6.54882 9.15829 6.54882 8.84171 6.35355 8.64645C6.15829 8.45118 5.84171 8.45118 5.64645 8.64645L3.14645 11.1464C2.95118 11.3417 2.95118 11.6583 3.14645 11.8536L5.64645 14.3536C5.84171 14.5488 6.15829 14.5488 6.35355 14.3536C6.54882 14.1583 6.54882 13.8417 6.35355 13.6464L4.70711 12H12.5C12.7761 12 13 11.7761 13 11.5C13 11.2239 12.7761 11 12.5 11H4.70711L6.35355 9.35355Z");

    /// <summary>Copy a reproduction script to the clipboard.</summary>
    /// <remarks>Fluent UI System Icons <c>ic_fluent_copy_16_regular</c>.</remarks>
    public static StreamGeometry CopyRepro { get; } = StreamGeometry.Parse(
        "M5 6H4C3.44772 6 3 6.44772 3 7V12C3 12.5523 3.44772 13 4 13H8C8.55228 13 9 12.5523 9 12H10C10 13.1046 9.10457 14 8 14H4C2.89543 14 2 13.1046 2 12V7C2 5.89543 2.89543 5 4 5H5V6ZM12 2C13.1046 2 14 2.89543 14 4V9C14 10.1046 13.1046 11 12 11H8C6.89543 11 6 10.1046 6 9V4C6 2.89543 6.89543 2 8 2H12ZM8 3C7.44772 3 7 3.44772 7 4V9C7 9.55228 7.44772 10 8 10H12C12.5523 10 13 9.55228 13 9V4C13 3.44772 12.5523 3 12 3H8Z");

    /// <summary>Execute a query and capture its actual plan.</summary>
    /// <remarks>Fluent UI System Icons <c>ic_fluent_play_16_regular</c>.</remarks>
    public static StreamGeometry RunRepro { get; } = StreamGeometry.Parse(
        "M5.74514 3.06445C5.41183 2.87696 5 3.11781 5 3.50023V12.5005C5 12.8829 5.41182 13.1238 5.74512 12.9363L13.7454 8.43631C14.0852 8.24517 14.0852 7.75589 13.7454 7.56474L5.74514 3.06445ZM4 3.50023C4 2.35298 5.2355 1.63041 6.23541 2.19288L14.2357 6.69317C15.2551 7.26664 15.2551 8.73446 14.2356 9.3079L6.23537 13.8079C5.23546 14.3703 4 13.6477 4 12.5005V3.50023Z");

    /// <summary>Query Store.</summary>
    /// <remarks>Fluent UI System Icons <c>ic_fluent_data_bar_vertical_16_regular</c>.</remarks>
    public static StreamGeometry QueryStore { get; } = StreamGeometry.Parse(
        "M2 3.5C2 2.67157 2.67157 2 3.5 2C4.32843 2 5 2.67157 5 3.5V12.5C5 13.3284 4.32843 14 3.5 14C2.67157 14 2 13.3284 2 12.5V3.5ZM3.5 3C3.22386 3 3 3.22386 3 3.5V12.5C3 12.7761 3.22386 13 3.5 13C3.77614 13 4 12.7761 4 12.5V3.5C4 3.22386 3.77614 3 3.5 3ZM6 6.5C6 5.67157 6.67157 5 7.5 5C8.32843 5 9 5.67157 9 6.5V12.5C9 13.3284 8.32843 14 7.5 14C6.67157 14 6 13.3284 6 12.5V6.5ZM7.5 6C7.22386 6 7 6.22386 7 6.5V12.5C7 12.7761 7.22386 13 7.5 13C7.77614 13 8 12.7761 8 12.5V6.5C8 6.22386 7.77614 6 7.5 6ZM11.5 8C10.6716 8 10 8.67157 10 9.5V12.5C10 13.3284 10.6716 14 11.5 14C12.3284 14 13 13.3284 13 12.5V9.5C13 8.67157 12.3284 8 11.5 8ZM11 9.5C11 9.22386 11.2239 9 11.5 9C11.7761 9 12 9.22386 12 9.5V12.5C12 12.7761 11.7761 13 11.5 13C11.2239 13 11 12.7761 11 12.5V9.5Z");

    /// <summary>A dashboard or overview surface.</summary>
    /// <remarks>Fluent UI System Icons <c>ic_fluent_grid_16_regular</c>.</remarks>
    public static StreamGeometry Overview { get; } = StreamGeometry.Parse(
        "M3.5 2C2.67157 2 2 2.67157 2 3.5V5.5C2 6.32843 2.67157 7 3.5 7H5.5C6.32843 7 7 6.32843 7 5.5V3.5C7 2.67157 6.32843 2 5.5 2H3.5ZM3 3.5C3 3.22386 3.22386 3 3.5 3H5.5C5.77614 3 6 3.22386 6 3.5V5.5C6 5.77614 5.77614 6 5.5 6H3.5C3.22386 6 3 5.77614 3 5.5V3.5ZM10.5 2C9.67157 2 9 2.67157 9 3.5V5.5C9 6.32843 9.67157 7 10.5 7H12.5C13.3284 7 14 6.32843 14 5.5V3.5C14 2.67157 13.3284 2 12.5 2H10.5ZM10 3.5C10 3.22386 10.2239 3 10.5 3H12.5C12.7761 3 13 3.22386 13 3.5V5.5C13 5.77614 12.7761 6 12.5 6H10.5C10.2239 6 10 5.77614 10 5.5V3.5ZM2 10.5C2 9.67157 2.67157 9 3.5 9H5.5C6.32843 9 7 9.67157 7 10.5V12.5C7 13.3284 6.32843 14 5.5 14H3.5C2.67157 14 2 13.3284 2 12.5V10.5ZM3.5 10C3.22386 10 3 10.2239 3 10.5V12.5C3 12.7761 3.22386 13 3.5 13H5.5C5.77614 13 6 12.7761 6 12.5V10.5C6 10.2239 5.77614 10 5.5 10H3.5ZM10.5 9C9.67157 9 9 9.67157 9 10.5V12.5C9 13.3284 9.67157 14 10.5 14H12.5C13.3284 14 14 13.3284 14 12.5V10.5C14 9.67157 13.3284 9 12.5 9H10.5ZM10 10.5C10 10.2239 10.2239 10 10.5 10H12.5C12.7761 10 13 10.2239 13 10.5V12.5C13 12.7761 12.7761 13 12.5 13H10.5C10.2239 13 10 12.7761 10 12.5V10.5Z");

    /// <summary>Format and re-indent SQL text.</summary>
    /// <remarks>Fluent UI System Icons <c>ic_fluent_text_align_left_16_regular</c>.</remarks>
    public static StreamGeometry Format { get; } = StreamGeometry.Parse(
        "M1 3.5C1 3.22386 1.22386 3 1.5 3H10.5C10.7761 3 11 3.22386 11 3.5C11 3.77614 10.7761 4 10.5 4H1.5C1.22386 4 1 3.77614 1 3.5ZM1 7.5C1 7.22386 1.22386 7 1.5 7H14.5C14.7761 7 15 7.22386 15 7.5C15 7.77614 14.7761 8 14.5 8H1.5C1.22386 8 1 7.77614 1 7.5ZM1 11.5C1 11.2239 1.22386 11 1.5 11H6.5C6.77614 11 7 11.2239 7 11.5C7 11.7761 6.77614 12 6.5 12H1.5C1.22386 12 1 11.7761 1 11.5Z");

    /// <summary>Connect to a server.</summary>
    /// <remarks>Fluent UI System Icons <c>ic_fluent_plug_connected_16_regular</c>.</remarks>
    public static StreamGeometry Connect { get; } = StreamGeometry.Parse(
        "M6.01323 6.77501C5.52723 6.28801 4.73223 6.28801 4.24523 6.77501L4.02523 6.99501C3.34923 7.67001 2.98523 8.56901 3.00023 9.52401C3.01223 10.28 3.26023 10.994 3.71023 11.583L1.64723 13.646C1.45223 13.841 1.45223 14.158 1.64723 14.353C1.74523 14.451 1.87323 14.499 2.00123 14.499C2.12923 14.499 2.25723 14.45 2.35523 14.353L4.42523 12.283C5.02223 12.718 5.73723 12.935 6.46123 12.935C7.39923 12.935 8.34923 12.572 9.06523 11.855L9.19623 11.724C9.68323 11.237 9.68323 10.444 9.19623 9.95601L6.01423 6.77401L6.01323 6.77501ZM8.48823 11.018L8.35723 11.149C7.36323 12.144 5.76123 12.208 4.78923 11.291C4.29123 10.823 4.01223 10.19 4.00123 9.50801C3.99123 8.82601 4.25123 8.18401 4.73323 7.70201L4.95323 7.48201C5.00223 7.43301 5.06523 7.40901 5.13023 7.40901C5.19523 7.40901 5.25823 7.43301 5.30723 7.48201L8.48923 10.664C8.58723 10.762 8.58723 10.92 8.48923 11.018H8.48823ZM14.3542 1.64601C14.1592 1.45101 13.8422 1.45101 13.6472 1.64601L11.5772 3.71601C10.2072 2.71701 8.20723 2.87301 6.93723 4.14401L6.80623 4.27501C6.31923 4.76201 6.31923 5.55501 6.80623 6.04301L9.98823 9.22501C10.2312 9.46901 10.5512 9.59101 10.8722 9.59101C11.1932 9.59101 11.5132 9.46901 11.7562 9.22501L11.9762 9.00501C12.6522 8.33001 13.0162 7.43101 13.0012 6.47601C12.9892 5.72001 12.7412 5.00601 12.2912 4.41701L14.3542 2.35401C14.5492 2.15901 14.5492 1.84101 14.3542 1.64601ZM11.2682 8.29701L11.0482 8.51701C10.9502 8.61501 10.7922 8.61501 10.6942 8.51701L7.51223 5.33501C7.41423 5.23701 7.41423 5.07901 7.51223 4.98101L7.64323 4.85001C8.16723 4.32601 8.86023 4.06001 9.54023 4.06001C10.1502 4.06001 10.7512 4.27401 11.2112 4.70801C11.7092 5.17601 11.9882 5.80901 11.9992 6.49101C12.0092 7.17301 11.7502 7.81501 11.2682 8.29701Z");

    /// <summary>Save to disk.</summary>
    /// <remarks>Fluent UI System Icons <c>ic_fluent_save_16_regular</c>.</remarks>
    public static StreamGeometry Save { get; } = StreamGeometry.Parse(
        "M4.00018 3C3.4479 3 3.00018 3.44772 3.00018 4V12C3.00018 12.5523 3.4479 13 4.00018 13V9.5C4.00018 8.67157 4.67176 8 5.50018 8H10.5002C11.3286 8 12.0002 8.67157 12.0002 9.5V13C12.5525 13 13.0002 12.5523 13.0002 12V5.62132C13.0002 5.3561 12.8948 5.10175 12.7073 4.91421L11.086 3.29289C10.8984 3.10536 10.6441 3 10.3789 3H10.0002V4.5C10.0002 5.32843 9.32861 6 8.50018 6H6.50018C5.67176 6 5.00018 5.32843 5.00018 4.5V3H4.00018ZM6.00018 3V4.5C6.00018 4.77614 6.22404 5 6.50018 5H8.50018C8.77633 5 9.00018 4.77614 9.00018 4.5V3H6.00018ZM11.0002 13V9.5C11.0002 9.22386 10.7763 9 10.5002 9H5.50018C5.22404 9 5.00018 9.22386 5.00018 9.5V13H11.0002ZM2.00018 4C2.00018 2.89543 2.89561 2 4.00018 2H10.3789C10.9093 2 11.418 2.21071 11.7931 2.58579L13.4144 4.20711C13.7895 4.58218 14.0002 5.09089 14.0002 5.62132V12C14.0002 13.1046 13.1048 14 12.0002 14H4.00018C2.89561 14 2.00018 13.1046 2.00018 12V4Z");

    /// <summary>The statement list of a batch or a procedure.</summary>
    /// <remarks>Fluent UI System Icons <c>ic_fluent_list_16_regular</c>.</remarks>
    public static StreamGeometry Statements { get; } = StreamGeometry.Parse(
        "M2 3.5C2 3.22386 2.22386 3 2.5 3H10.5C10.7761 3 11 3.22386 11 3.5C11 3.77614 10.7761 4 10.5 4H2.5C2.22386 4 2 3.77614 2 3.5ZM2 11.5C2 11.2239 2.22386 11 2.5 11H9.5C9.77614 11 10 11.2239 10 11.5C10 11.7761 9.77614 12 9.5 12H2.5C2.22386 12 2 11.7761 2 11.5ZM2.5 7C2.22386 7 2 7.22386 2 7.5C2 7.77614 2.22386 8 2.5 8H13.5C13.7761 8 14 7.77614 14 7.5C14 7.22386 13.7761 7 13.5 7H2.5Z");

    /// <summary>Zoom in.</summary>
    /// <remarks>Fluent UI System Icons <c>ic_fluent_zoom_in_16_regular</c>.</remarks>
    public static StreamGeometry ZoomIn { get; } = StreamGeometry.Parse(
        "M6.5 4C6.77614 4 7 4.22386 7 4.5V6H8.5C8.77614 6 9 6.22386 9 6.5C9 6.77614 8.77614 7 8.5 7H7V8.5C7 8.77614 6.77614 9 6.5 9C6.22386 9 6 8.77614 6 8.5V7H4.5C4.22386 7 4 6.77614 4 6.5C4 6.22386 4.22386 6 4.5 6H6V4.5C6 4.22386 6.22386 4 6.5 4ZM6.5 1C9.53757 1 12 3.46243 12 6.5C12 7.83875 11.5216 9.06578 10.7266 10.0195L13.8535 13.1465C14.0488 13.3417 14.0488 13.6583 13.8535 13.8535C13.6583 14.0488 13.3417 14.0488 13.1465 13.8535L10.0195 10.7266C9.06578 11.5216 7.83875 12 6.5 12C3.46243 12 1 9.53757 1 6.5C1 3.46243 3.46243 1 6.5 1ZM6.5 2C4.01472 2 2 4.01472 2 6.5C2 8.98528 4.01472 11 6.5 11C8.98528 11 11 8.98528 11 6.5C11 4.01472 8.98528 2 6.5 2Z");

    /// <summary>Zoom out.</summary>
    /// <remarks>Fluent UI System Icons <c>ic_fluent_zoom_out_16_regular</c>.</remarks>
    public static StreamGeometry ZoomOut { get; } = StreamGeometry.Parse(
        "M8.5 6C8.77614 6 9 6.22386 9 6.5C9 6.77614 8.77614 7 8.5 7H4.5C4.22386 7 4 6.77614 4 6.5C4 6.22386 4.22386 6 4.5 6H8.5ZM6.5 1C9.53757 1 12 3.46243 12 6.5C12 7.83875 11.5216 9.06578 10.7266 10.0195L13.8535 13.1465C14.0488 13.3417 14.0488 13.6583 13.8535 13.8535C13.6583 14.0488 13.3417 14.0488 13.1465 13.8535L10.0195 10.7266C9.06578 11.5216 7.83875 12 6.5 12C3.46243 12 1 9.53757 1 6.5C1 3.46243 3.46243 1 6.5 1ZM6.5 2C4.01472 2 2 4.01472 2 6.5C2 8.98528 4.01472 11 6.5 11C8.98528 11 11 8.98528 11 6.5C11 4.01472 8.98528 2 6.5 2Z");

    /// <summary>Zoom the plan to fit the viewport.</summary>
    /// <remarks>Fluent UI System Icons <c>ic_fluent_full_screen_maximize_16_regular</c>.</remarks>
    public static StreamGeometry ZoomFit { get; } = StreamGeometry.Parse(
        "M3.75 3C3.33579 3 3 3.33579 3 3.75V5.5C3 5.77614 2.77614 6 2.5 6C2.22386 6 2 5.77614 2 5.5V3.75C2 2.7835 2.7835 2 3.75 2H5.5C5.77614 2 6 2.22386 6 2.5C6 2.77614 5.77614 3 5.5 3H3.75ZM10 2.5C10 2.22386 10.2239 2 10.5 2H12.25C13.2165 2 14 2.7835 14 3.75V5.5C14 5.77614 13.7761 6 13.5 6C13.2239 6 13 5.77614 13 5.5V3.75C13 3.33579 12.6642 3 12.25 3H10.5C10.2239 3 10 2.77614 10 2.5ZM2.5 10C2.77614 10 3 10.2239 3 10.5V12.25C3 12.6642 3.33579 13 3.75 13H5.5C5.77614 13 6 13.2239 6 13.5C6 13.7761 5.77614 14 5.5 14H3.75C2.7835 14 2 13.2165 2 12.25V10.5C2 10.2239 2.22386 10 2.5 10ZM13.5 10C13.7761 10 14 10.2239 14 10.5V12.25C14 13.2165 13.2165 14 12.25 14H10.5C10.2239 14 10 13.7761 10 13.5C10 13.2239 10.2239 13 10.5 13H12.25C12.6642 13 13 12.6642 13 12.25V10.5C13 10.2239 13.2239 10 13.5 10Z");

    /// <summary>Open a file.</summary>
    /// <remarks>Fluent UI System Icons <c>ic_fluent_folder_open_16_regular</c>.</remarks>
    public static StreamGeometry OpenFile { get; } = StreamGeometry.Parse(
        "M2 4.5V9.10022L2.92389 7.5C3.45979 6.5718 4.45017 6 5.52196 6L11.9146 6C11.7087 5.4174 11.1531 5 10.5 5H7C6.86739 5 6.74021 4.94732 6.64645 4.85355L4.93934 3.14645C4.84557 3.05268 4.71839 3 4.58579 3H3.5C2.67157 3 2 3.67157 2 4.5ZM7.06895 13.9953C7.04641 13.9984 7.02339 14 7 14H3.5C2.11929 14 1 12.8807 1 11.5V4.5C1 3.11929 2.11929 2 3.5 2H4.58579C4.98361 2 5.36514 2.15804 5.64645 2.43934L7.20711 4H10.5C11.724 4 12.7426 4.87965 12.958 6.04127C14.605 6.34148 15.5443 8.22106 14.6616 9.75L13.0766 12.4953C12.5407 13.4235 11.5503 13.9953 10.4785 13.9953H7.06895ZM5.52196 7C4.80743 7 4.14718 7.3812 3.78991 8L2.20492 10.7453C1.62757 11.7453 2.34926 12.9953 3.50396 12.9953L10.4785 12.9953C11.193 12.9953 11.8533 12.6141 12.2105 11.9953L13.7955 9.25C14.3729 8.25 13.6512 7 12.4965 7L5.52196 7Z");

    /// <summary>Paste plan XML off the clipboard.</summary>
    /// <remarks>Fluent UI System Icons <c>ic_fluent_clipboard_paste_16_regular</c>.</remarks>
    public static StreamGeometry PastePlan { get; } = StreamGeometry.Parse(
        "M4.08535 2H3.5C3.10218 2 2.72064 2.15804 2.43934 2.43934C2.15804 2.72064 2 3.10218 2 3.5V13.5C2 13.8978 2.15804 14.2794 2.43934 14.5607C2.72064 14.842 3.10217 15 3.5 15H6.1115V14H3.5C3.36739 14 3.24021 13.9473 3.14645 13.8536C3.05268 13.7598 3 13.6326 3 13.5V3.5C3 3.36739 3.05268 3.24021 3.14645 3.14645C3.24021 3.05268 3.36739 3 3.5 3H4.08535C4.29127 3.5826 4.84689 4 5.5 4H8.5C9.15311 4 9.70873 3.5826 9.91465 3H10.5C10.6326 3 10.7598 3.05268 10.8536 3.14645C10.9473 3.24022 11 3.36739 11 3.5V5H12V3.5C12 3.10217 11.842 2.72064 11.5607 2.43934C11.2794 2.15804 10.8978 2 10.5 2H9.91465C9.70873 1.4174 9.15311 1 8.5 1H5.5C4.84689 1 4.29127 1.4174 4.08535 2ZM5 2.5C5 2.22386 5.22386 2 5.5 2H8.5C8.77614 2 9 2.22386 9 2.5C9 2.77614 8.77614 3 8.5 3H5.5C5.22386 3 5 2.77614 5 2.5ZM8.5 6C7.67157 6 7 6.67157 7 7.5V13.5C7 14.3284 7.67157 15 8.5 15H12.5C13.3284 15 14 14.3284 14 13.5V7.5C14 6.67157 13.3284 6 12.5 6H8.5ZM8 7.5C8 7.22386 8.22386 7 8.5 7H12.5C12.7761 7 13 7.22386 13 7.5V13.5C13 13.7761 12.7761 14 12.5 14H8.5C8.22386 14 8 13.7761 8 13.5V7.5Z");

    /// <summary>Toggle the plan minimap.</summary>
    /// <remarks>Fluent UI System Icons <c>ic_fluent_map_16_regular</c>.</remarks>
    public static StreamGeometry Minimap { get; } = StreamGeometry.Parse(
        "M5.235 2.076C5.38271 1.98368 5.56781 1.97489 5.72361 2.05279L10.4728 4.42738L14.235 2.076C14.3891 1.97967 14.5834 1.97457 14.7424 2.06268C14.9014 2.15079 15 2.31824 15 2.5V11C15 11.1724 14.9112 11.3326 14.765 11.424L10.765 13.924C10.6173 14.0163 10.4322 14.0251 10.2764 13.9472L5.52721 11.5726L1.765 13.924C1.61087 14.0203 1.41659 14.0254 1.25762 13.9373C1.09864 13.8492 1 13.6818 1 13.5V5C1 4.82761 1.08881 4.66737 1.235 4.576L5.235 2.076ZM6 10.691L10 12.691V5.30902L6 3.30902V10.691ZM5 3.40212L2 5.27712V12.5979L5 10.7229V3.40212ZM11 5.27712V12.5979L14 10.7229V3.40212L11 5.27712Z");

    /// <summary>
    /// The theme <see cref="MakeIcon"/> puts on its <see cref="PathIcon"/>, replacing the Fluent
    /// one for two reasons.
    ///
    /// <para><b>Color.</b> Avalonia's stock PathIcon theme sets <c>Foreground</c> to
    /// <c>TextControlForeground</c>. A theme setter beats an inherited value, so a plain PathIcon
    /// does <i>not</i> pick up the foreground of the button it sits in — it would stay the Fluent
    /// white while the label beside it is #E4E6EB, and it would not go white with the label on
    /// hover. Leaving Foreground unset here lets ordinary inheritance do its job, which is what
    /// the AppButton hover style (it sets Foreground on the ContentPresenter) needs.</para>
    ///
    /// <para><b>Weight.</b> The stock template fits the icon's <i>ink</i> to the control, so each
    /// icon gets its own scale factor — the boxy ones (copy, grid, save) would render about 17%
    /// heavier than the ones that fill their box vertically (bot, map, folder). Scaling the whole
    /// 16x16 frame instead gives every icon the same 14/16 scale, the same stroke weight, and its
    /// designed position inside the frame. The fixed-size <see cref="Canvas"/> is what pins the
    /// frame: a <c>Path</c> with <c>Stretch.None</c> reports only the extent of its own ink,
    /// while the Canvas always measures 16x16.</para>
    /// </summary>
    private static readonly ControlTheme FramedIconTheme = new(typeof(PathIcon))
    {
        Setters =
        {
            new Setter(TemplatedControl.TemplateProperty, new FuncControlTemplate<PathIcon>((icon, _) =>
                new Viewbox
                {
                    Stretch = Stretch.Uniform,
                    Child = new Canvas
                    {
                        Width = IconFrame,
                        Height = IconFrame,
                        Children =
                        {
                            new Shapes.Path
                            {
                                Stretch = Stretch.None,
                                [!Shapes.Path.DataProperty] = icon[!PathIcon.DataProperty],
                                [!Shapes.Path.FillProperty] = icon[!TemplatedControl.ForegroundProperty]
                            }
                        }
                    }
                }))
        }
    };

    /// <summary>
    /// Wraps one of the geometries above in the control that goes beside a button's label.
    /// Deliberately no Foreground: it inherits whatever the button's content presenter has,
    /// including the white that AppButton's hover state sets.
    /// </summary>
    public static Control MakeIcon(StreamGeometry geometry) =>
        new PathIcon
        {
            Data = geometry,
            Width = IconSize,
            Height = IconSize,
            VerticalAlignment = VerticalAlignment.Center,
            Theme = FramedIconTheme
        };

    /// <summary>
    /// The whole content of an icon-and-label toolbar button, so every such button is spaced and
    /// aligned identically. Assign it to <c>Button.Content</c>.
    /// </summary>
    public static Control MakeContent(StreamGeometry geometry, string label) =>
        new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = IconLabelGap,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                MakeIcon(geometry),
                new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }
            }
        };

    /// <summary>Gap between a toolbar icon and its label.</summary>
    internal const double IconLabelGap = 6;
}

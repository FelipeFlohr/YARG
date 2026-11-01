using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace YARG.Menu.Web
{
    /// <summary>
    /// Builds the plain uGUI pieces of the screens made in code (the web song screen, the online screen), in one
    /// shared look, with the menus' font.
    /// </summary>
    public sealed class UiBuilder
    {
        public static readonly Color BackgroundColor = new(0.05f, 0.06f, 0.09f, 1f);
        public static readonly Color PanelColor = new(0.11f, 0.13f, 0.18f, 1f);
        public static readonly Color RowColor = new(0.14f, 0.16f, 0.22f, 1f);
        public static readonly Color FieldColor = new(0.05f, 0.06f, 0.09f, 1f);
        public static readonly Color ButtonColor = new(0.18f, 0.22f, 0.32f, 1f);
        public static readonly Color PrimaryButtonColor = new(0.16f, 0.55f, 0.32f, 1f);
        public static readonly Color DangerButtonColor = new(0.23f, 0.13f, 0.15f, 1f);
        public static readonly Color TextColor = new(0.95f, 0.96f, 0.98f, 1f);
        public static readonly Color DimTextColor = new(0.65f, 0.69f, 0.78f, 1f);
        public static readonly Color ErrorColor = new(1f, 0.42f, 0.42f, 1f);
        public static readonly Color WarningColor = new(1f, 0.77f, 0.42f, 1f);

        private readonly TMP_FontAsset _font;

        public UiBuilder(TMP_FontAsset font)
        {
            _font = font;
        }

        /// <summary>
        /// A screen-space canvas for a whole menu, scaled from 1920x1080.
        /// </summary>
        public static GameObject CreateCanvasObject(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.SetActive(false);
            go.transform.SetParent(parent, false);

            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            go.AddComponent<GraphicRaycaster>();
            return go;
        }

        public static RectTransform Rect(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform) go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return rect;
        }

        /// <summary>
        /// A child of a layout group, with the height (and optionally width) it asks for.
        /// </summary>
        public static RectTransform LayoutRect(string name, Transform parent, float height, float width = -1)
        {
            var rect = Rect(name, parent, Vector2.zero, Vector2.one);
            var element = rect.gameObject.AddComponent<LayoutElement>();
            element.preferredHeight = height;
            element.minHeight = height;
            // Otherwise a layout group on the same object passes its children's flexible height up and the row grows
            element.flexibleHeight = 0;
            if (width >= 0)
            {
                element.preferredWidth = width;
                element.minWidth = width;
            }
            else
            {
                element.flexibleWidth = 1;
            }
            return rect;
        }

        public static Image Image(RectTransform rect, Color color)
        {
            var image = rect.gameObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        public static VerticalLayoutGroup Column(RectTransform rect, float spacing, int padding)
        {
            var layout = rect.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = spacing;
            layout.padding = new RectOffset(padding, padding, padding, padding);
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            return layout;
        }

        public static HorizontalLayoutGroup Row(RectTransform rect, float spacing)
        {
            var layout = rect.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = spacing;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;
            return layout;
        }

        public TextMeshProUGUI Text(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax, float size,
            FontStyles style, TextAlignmentOptions alignment, Color color)
        {
            return SetUpText(Rect(name, parent, anchorMin, anchorMax), size, style, alignment, color);
        }

        public TextMeshProUGUI LayoutText(string name, Transform parent, float height, float size, FontStyles style,
            TextAlignmentOptions alignment, Color color)
        {
            return SetUpText(LayoutRect(name, parent, height), size, style, alignment, color);
        }

        private TextMeshProUGUI SetUpText(RectTransform rect, float size, FontStyles style,
            TextAlignmentOptions alignment, Color color)
        {
            var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            if (_font != null)
            {
                text.font = _font;
            }
            text.fontSize = size;
            text.fontStyle = style;
            text.alignment = alignment;
            text.color = color;
            text.richText = true;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.raycastTarget = false;
            return text;
        }

        /// <summary>
        /// A flat button filling <paramref name="rect"/>, with a centered label.
        /// </summary>
        public Button Button(RectTransform rect, Color color, UnityAction onClick, out TextMeshProUGUI label,
            float fontSize = 34)
        {
            var image = Image(rect, color);
            image.raycastTarget = true;

            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            var colors = button.colors;
            colors.disabledColor = new Color(0.55f, 0.55f, 0.55f, 0.6f);
            button.colors = colors;
            button.onClick.AddListener(onClick);

            label = Text("Label", rect, Vector2.zero, Vector2.one, fontSize, FontStyles.Bold,
                TextAlignmentOptions.Center, TextColor);
            label.margin = new Vector4(16, 0, 16, 0);
            return button;
        }

        public Button Button(string name, Transform parent, Color color, UnityAction onClick,
            out TextMeshProUGUI label)
        {
            return Button(Rect(name, parent, Vector2.zero, Vector2.one), color, onClick, out label);
        }

        /// <summary>
        /// A one-line text field filling <paramref name="rect"/>. The menus ignore their keys while it has focus.
        /// </summary>
        public TMP_InputField InputField(RectTransform rect, string placeholder, bool password, int characterLimit,
            float fontSize = 32)
        {
            var background = Image(rect, FieldColor);
            background.raycastTarget = true;

            var area = Rect("TextArea", rect, Vector2.zero, Vector2.one);
            area.offsetMin = new Vector2(20, 4);
            area.offsetMax = new Vector2(-20, -4);
            area.gameObject.AddComponent<RectMask2D>();

            var hint = Text("Placeholder", area, Vector2.zero, Vector2.one, fontSize, FontStyles.Italic,
                TextAlignmentOptions.Left, DimTextColor);
            hint.text = placeholder;
            hint.textWrappingMode = TextWrappingModes.NoWrap;

            var text = Text("Text", area, Vector2.zero, Vector2.one, fontSize, FontStyles.Normal,
                TextAlignmentOptions.Left, TextColor);
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Overflow;

            var input = rect.gameObject.AddComponent<TMP_InputField>();
            input.targetGraphic = background;
            input.textViewport = area;
            input.textComponent = text;
            input.placeholder = hint;
            if (_font != null)
            {
                input.fontAsset = _font;
            }
            input.pointSize = fontSize;
            input.characterLimit = characterLimit;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.contentType = password ? TMP_InputField.ContentType.Password : TMP_InputField.ContentType.Standard;
            input.caretColor = TextColor;
            input.customCaretColor = true;
            input.selectionColor = new Color(0.27f, 0.83f, 0.51f, 0.35f);
            return input;
        }
    }
}

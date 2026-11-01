using UnityEngine;
using UnityEngine.Assertions;

namespace YARG.Menu
{
    public class MenuObject : MonoBehaviour
    {
        [field: SerializeField]
        public MenuManager.Menu Menu { get; private set; }

        [field: SerializeField]
        public bool HideBelow { get; private set; } = true;

        /// <summary>For menus built in code instead of a prefab. Call before the object is first enabled.</summary>
        public void SetUp(MenuManager.Menu menu, bool hideBelow)
        {
            Menu = menu;
            HideBelow = hideBelow;
        }

        private void Start()
        {
            Assert.AreNotEqual(Menu, MenuManager.Menu.None);
        }

        public void CloseMenu()
        {
            MenuManager.Instance.PopMenu();
        }
    }
}
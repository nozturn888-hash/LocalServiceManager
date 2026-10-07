using System;
using System.Drawing;
using System.Windows.Forms;
namespace LocalServiceManager
{
    static class TrayPlacement
    {
        public static Rectangle Fit(Rectangle bounds,Rectangle work)
        {
            int width=Math.Min(bounds.Width,work.Width),height=Math.Min(bounds.Height,work.Height);
            return new Rectangle(Math.Max(work.Left,Math.Min(bounds.X,work.Right-width)),Math.Max(work.Top,Math.Min(bounds.Y,work.Bottom-height)),width,height);
        }
        public static Rectangle Submenu(Rectangle parent,Size size,Rectangle work)
        {
            int left=parent.Left-size.Width+2,right=parent.Right-2;
            int x=right+size.Width<=work.Right?right:left>=work.Left?left:right;
            return Fit(new Rectangle(new Point(x,parent.Top),size),work);
        }
    }
    sealed class TrayDropDown : ToolStripDropDownMenu
    {
        public Rectangle WorkArea;
        protected override void SetBoundsCore(int x,int y,int width,int height,BoundsSpecified specified)
        {
            if(!WorkArea.IsEmpty){var bounds=TrayPlacement.Fit(new Rectangle(x,y,width,height),WorkArea);x=bounds.X;y=bounds.Y;width=bounds.Width;height=bounds.Height;}
            base.SetBoundsCore(x,y,width,height,specified);
        }
    }
}

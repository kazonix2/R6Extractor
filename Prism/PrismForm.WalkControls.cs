using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using OpenTK;

namespace Prism;

public partial class PrismForm
{
	private readonly HashSet<Keys> _walkKeys = new();
	private readonly Stopwatch _walkClock = new();
	private Timer _walkTimer;
	private bool _walkToggleDown;

	private void SetupWalkControls()
	{
		_walkTimer = new Timer { Interval = 15 };
		_walkTimer.Tick += (_, _) =>
		{
			var elapsed = (float)_walkClock.Elapsed.TotalSeconds;
			_walkClock.Restart();
			var movement = new Vector3(Axis(Keys.D, Keys.A), Axis(Keys.Space, Keys.ControlKey), Axis(Keys.S, Keys.W));
			_renderer3d.WalkMove(movement, elapsed, _walkKeys.Contains(Keys.ShiftKey));
		};
		_glControl.PreviewKeyDown += (_, args) =>
		{
			if (_renderer3d.WalkMode && args.KeyCode is Keys.Space or Keys.Escape)
				args.IsInputKey = true;
		};
		_glControl.KeyDown += (_, args) =>
		{
			if (args.KeyCode == Keys.G)
			{
				if (!_walkToggleDown)
				{
					_walkToggleDown = true;
					if (_renderer3d.WalkMode)
						EndWalkMode();
					else
						BeginWalkMode();
				}
				args.Handled = true;
			}
			else if (_renderer3d.WalkMode)
			{
				if (args.KeyCode == Keys.Escape)
					EndWalkMode();
				else
					_walkKeys.Add(args.KeyCode);
				args.Handled = true;
			}
		};
		_glControl.KeyUp += (_, args) =>
		{
			if (args.KeyCode == Keys.G)
				_walkToggleDown = false;
			_walkKeys.Remove(args.KeyCode);
		};
		_glControl.LostFocus += (_, _) =>
		{
			_walkToggleDown = false;
			EndWalkMode();
		};
		Deactivate += (_, _) =>
		{
			_walkToggleDown = false;
			EndWalkMode();
		};
		FormClosed += (_, _) =>
		{
			EndWalkMode();
			_walkTimer.Dispose();
		};
	}

	private int Axis(Keys positive, Keys negative) =>
		(_walkKeys.Contains(positive) ? 1 : 0) - (_walkKeys.Contains(negative) ? 1 : 0);

	private void BeginWalkMode()
	{
		Cursor.Position = _glControl.PointToScreen(new Point(_glControl.Width / 2, _glControl.Height / 2));
		_renderer3d.SetWalkMode(true);
		Cursor.Hide();
		_walkClock.Restart();
		_walkTimer.Start();
	}

	private void EndWalkMode()
	{
		if (_renderer3d?.WalkMode != true)
			return;
		_walkTimer.Stop();
		_walkKeys.Clear();
		_renderer3d.SetWalkMode(false);
		Cursor.Show();
	}
}

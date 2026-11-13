using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Mia.Services;

namespace Mia;

// Opt-in release validation. Only the separately launched MIA fixture is moved or minimized.
internal static class InteractionDiagnostics
{
    internal static void RunFixture(string directory)
    {
        var application=new Application();
        var window=new Window { Title="MIA · 停靠驗證用視窗",Width=480,Height=240,Topmost=true,
            Background=new SolidColorBrush(Color.FromRgb(44,48,52)),Foreground=Brushes.White,
            Content=new TextBlock { Text="米亞視窗互動驗證\n此視窗會移動、縮小，然後自動關閉。",Margin=new Thickness(30),FontSize=20 } };
        var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(80) };
        string previous="";DateTime deadline=DateTime.UtcNow.AddSeconds(100);
        window.Loaded+=(_,_)=> {
            var handle=new WindowInteropHelper(window).Handle;
            NativeDesktop.GetCursorPos(out var cursor);var area=NativeDesktop.WorkArea(cursor);
            NativeDesktop.Position(handle,area.Left+80,area.Top+Math.Min(420,area.Height-260));
            WriteSignal(Path.Combine(directory,"fixture-handle.txt"),handle.ToInt64().ToString());timer.Start();
        };
        timer.Tick+=(_,_)=> {
            if(DateTime.UtcNow>deadline){window.Close();return;}
            var file=Path.Combine(directory,"fixture-command.txt");
            if(!File.Exists(file))return;
            string command;try{command=ReadSignal(file);}catch(IOException){return;}
            if(command==previous)return;previous=command;
            var handle=new WindowInteropHelper(window).Handle;
            if(command=="move") {NativeDesktop.GetWindowRect(handle,out var r);NativeDesktop.Position(handle,r.Left+100,r.Top+30);}
            if(command=="headroom") {NativeDesktop.GetWindowRect(handle,out var r);var a=NativeDesktop.WorkArea(new NativeDesktop.Point {X=r.Left,Y=r.Top});NativeDesktop.Position(handle,r.Left,a.Top+200*VisualTreeHelper.GetDpi(window).DpiScaleY);}
            if(command.StartsWith("screen:"))
            {
                var xy=command[7..].Split(',');NativeDesktop.Position(handle,int.Parse(xy[0]),int.Parse(xy[1]));
            }
            if(command=="resize")window.Width=360;
            if(command.StartsWith("shake"))
            {
                int step=int.Parse(command[5..]);NativeDesktop.GetWindowRect(handle,out var r);
                double delta=160*VisualTreeHelper.GetDpi(window).DpiScaleX*(step%2==1?1:-1);
                NativeDesktop.Position(handle,r.Left+delta,r.Top);
            }
            if(command=="maximize")window.WindowState=WindowState.Maximized;
            if(command=="minimize")window.WindowState=WindowState.Minimized;
            if(command=="restore") {window.WindowState=WindowState.Normal;window.Activate();}
            WriteSignal(Path.Combine(directory,"fixture-ack.txt"),command);
            if(command=="close")window.Close();
        };
        window.Closed+=(_,_)=>timer.Stop();application.Run(window);
    }

    internal static async Task RunAsync(App app)
    {
        string directory=Path.Combine(SettingsStore.DataDirectory,"dock-run-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var checks=new Dictionary<string,bool>();Process? fixture=null;
        try
        {
            var start=new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute=false };
            start.ArgumentList.Add("--dock-fixture");start.ArgumentList.Add(directory);
            fixture=Process.Start(start)??throw new IOException("Fixture could not start.");
            await Until(()=>File.Exists(Path.Combine(directory,"fixture-handle.txt")));
            var handle=new IntPtr(long.Parse(ReadSignal(Path.Combine(directory,"fixture-handle.txt"))));
            await Task.Delay(350);
            await Until(()=>app.Pet.TryDockWindow(handle));
            checks["docked_to_own_fixture"]=true;
            await Task.Delay(600);
            var petHandle=new WindowInteropHelper(app.Pet).Handle;
            NativeDesktop.GetWindowRect(petHandle,out var p1);NativeDesktop.TrySurface(handle,out var f1);
            checks["seated_pose"]=app.Pet.CurrentPoseName=="perched";
            // Manifest seat anchor is 145 in a 208-pixel cell, independent of monitor scaling.
            checks["seat_aligned_to_edge"]=Math.Abs(p1.Top+p1.Height*145.0/208-f1.Top)<5;
            app.Pet.Paused=true;
            await Command("move");await Task.Delay(500);
            NativeDesktop.GetWindowRect(petHandle,out var p2);NativeDesktop.TrySurface(handle,out var f2);
            checks["followed_horizontal_move"]=Math.Abs((p2.Left-p1.Left)-(f2.Left-f1.Left))<4;
            checks["followed_vertical_move"]=Math.Abs((p2.Top-p1.Top)-(f2.Top-f1.Top))<4;
            checks["pause_still_tracks_window"]=app.Pet.DockedWindow==handle;
            app.Pet.Paused=false;
            await Command("resize");await Task.Delay(500);
            NativeDesktop.GetWindowRect(petHandle,out var pr);NativeDesktop.TrySurface(handle,out var fr);
            checks["resize_keeps_relative_position"]=Math.Abs(pr.Left+pr.Width/2.0-(fr.Left+fr.Width/2.0))<5;
            checks["resize_keeps_seat_on_edge"]=Math.Abs(pr.Top+pr.Height*145.0/208-fr.Top)<5;
            await Task.Delay(600);
            for(int step=1;step<=4;step++){await Command("shake"+step);await Task.Delay(70);}
            await Task.Delay(80);
            checks["deliberate_shake_releases_pet"]=app.Pet.DockedWindow==IntPtr.Zero;
            checks["can_redock_after_shake"]=app.Pet.TryDockWindow(handle);
            await Command("minimize");await Task.Delay(350);
            checks["released_minimized_window"]=app.Pet.DockedWindow==IntPtr.Zero;
            await Command("restore");await Task.Delay(250);
            checks["redocked_after_restore"]=app.Pet.TryDockWindow(handle);
            await Command("maximize");await Task.Delay(350);
            checks["maximized_window_releases_pet"]=app.Pet.DockedWindow==IntPtr.Zero;
            await Command("restore");await Task.Delay(250);
            checks["redocked_before_scale"]=app.Pet.TryDockWindow(handle);
            await Command("headroom");await Task.Delay(250);
            app.Settings.PetScale=3;app.ApplySettings();await Task.Delay(350);
            NativeDesktop.GetWindowRect(petHandle,out var scaled);
            checks["pet_scale_applied"]=Math.Abs(scaled.Width-p1.Width*3)<5;
            checks["enlarged_head_hits_top_and_releases"]=app.Pet.DockedWindow==IntPtr.Zero;
            app.Settings.PetScale=1;app.ApplySettings();await Task.Delay(250);
            app.Pet.TryDockWindow(handle);
            int screenIndex=0;
            foreach(var screen in NativeDesktop.WorkAreas())
            {
                screenIndex++;
                await Command($"screen:{screen.Left+40},{screen.Top+Math.Min(400,screen.Height/2)}");await Task.Delay(600);
                checks[$"screen_{screenIndex}_dock"]=app.Pet.TryDockWindow(handle);await Task.Delay(400);
                NativeDesktop.GetWindowRect(petHandle,out var onScreen);
                checks[$"screen_{screenIndex}_visible"]=onScreen.Left>=screen.Left && onScreen.Right<=screen.Right && onScreen.Top>=screen.Top;
                NativeDesktop.TrySurface(handle,out var target);
                checks[$"screen_{screenIndex}_seat"]=Math.Abs(onScreen.Top+onScreen.Height*145.0/208-target.Top)<5;
            }
            app.Pet.FocusTimer.Start(1);app.Pet.FocusTimer.Tick(61,false);
            checks["focus_reminder_ready"]=app.Pet.FocusTimer.Completed;app.Pet.StopFocus();
            app.Settings.Mode="companion";app.ApplySettings();app.Pet.ToggleBall();await Task.Delay(200);
            checks["toy_opened"]=Application.Current.Windows.OfType<ToyBallWindow>().Any();
            var toy=Application.Current.Windows.OfType<ToyBallWindow>().Single();
            foreach(double scale in new[]{.5,1,3})
            {
                app.Settings.PetScale=scale;app.ApplySettings();await Task.Delay(200);
                checks[$"toy_scale_{scale}"]=Math.Abs(toy.Width-42*scale)<.01;
                if(scale==3)
                {
                    var ballPreview=new System.Windows.Media.Imaging.RenderTargetBitmap(504,504,384,384,PixelFormats.Pbgra32);
                    ballPreview.Render(toy);var ballPng=new System.Windows.Media.Imaging.PngBitmapEncoder();
                    ballPng.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(ballPreview));
                    using(var output=File.Create(Path.Combine(SettingsStore.DataDirectory,"ping-pong-preview.png")))ballPng.Save(output);
                }
            }
            app.Settings.PetScale=1;app.ApplySettings();await Task.Delay(100);
            app.Pet.Paused=true;
            checks["ball_taken_by_pet"]=toy.TakeByPet();
            var ballArea=toy.Area;double handX=ballArea.Left+ballArea.Width/2.0,handY=ballArea.Top+ballArea.Height/2.0;
            toy.FollowHands(handX,handY);
            checks["ball_follows_hands"]=Math.Abs(toy.CenterX-handX)<2 && Math.Abs(toy.CenterY-handY)<2;
            toy.Tick(.05,false);
            checks["held_ball_ignores_gravity"]=Math.Abs(toy.CenterY-handY)<2;
            toy.ReleaseFromPet(200,-500);toy.Tick(.05,false);
            checks["toss_releases_and_rises"]=!toy.PetHeld && toy.CenterX>handX && toy.CenterY<handY;
            toy.TakeByPet();app.Pet.CancelBallPlay();
            checks["interruption_releases_held_ball"]=!toy.PetHeld;
            app.Pet.Paused=false;
            void PreparePlay(int kind)
            {
                app.Pet.CancelBallPlay();app.Pet.ReturnToCursor();
                NativeDesktop.GetWindowRect(petHandle,out var body);
                // Keep this rightward carry fixture away from screen edges, regardless of the user's cursor.
                var playArea=app.Pet.AllowedWorkArea(new NativeDesktop.Point {X=body.Left+body.Width/2,Y=body.Top+body.Height/2});
                NativeDesktop.Position(petHandle,playArea.Left+(playArea.Width-body.Width)/2.0,playArea.Bottom-body.Height*203.0/208);
                NativeDesktop.GetWindowRect(petHandle,out body);
                toy.TakeByPet();toy.FollowHands(body.Left+body.Width*.8,body.Top+body.Height*190.0/208);toy.ReleaseFromPet();
                void Set(string name,object value)=>typeof(PetWindow).GetField(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(app.Pet,value);
                Set("ballPlayKind",kind);Set("kickDirection",1d);Set("kickStarted",DateTime.UtcNow);Set("kickUntil",DateTime.UtcNow.AddSeconds(3));Set("kickApplied",false);Set("nextKick",DateTime.UtcNow.AddSeconds(5));Set("mood","idle");
            }
            PreparePlay(4);await Task.Delay(700);
            checks["carry_pickup_attaches_ball"]=toy.PetHeld && app.Pet.CurrentAnimation=="carryingball";
            NativeDesktop.GetWindowRect(petHandle,out var walkStart);
            await Task.Delay(500);NativeDesktop.GetWindowRect(petHandle,out var walkEnd);
            checks["carry_walk_moves_with_ball"]=toy.PetHeld && walkEnd.Left>walkStart.Left;
            WriteSignal(Path.Combine(directory,"carry-motion.json"),JsonSerializer.Serialize(new {held=toy.PetHeld,start=walkStart.Left,end=walkEnd.Left,pose=app.Pet.CurrentAnimation}));
            var heldPreview=new System.Windows.Media.Imaging.RenderTargetBitmap((int)app.Pet.ActualWidth,(int)app.Pet.ActualHeight,96,96,PixelFormats.Pbgra32);
            var visual=new DrawingVisual();
            using(var dc=visual.RenderOpen())
            {
                dc.DrawRectangle(new VisualBrush(app.Pet),null,new Rect(0,0,app.Pet.ActualWidth,app.Pet.ActualHeight));
                NativeDesktop.GetWindowRect(new WindowInteropHelper(toy).Handle,out var ballBounds);
                double dpi=walkEnd.Width/app.Pet.ActualWidth;
                dc.DrawRectangle(new VisualBrush(toy),null,new Rect((ballBounds.Left-walkEnd.Left)/dpi,(ballBounds.Top-walkEnd.Top)/dpi,ballBounds.Width/dpi,ballBounds.Height/dpi));
            }
            heldPreview.Render(visual);var heldPng=new System.Windows.Media.Imaging.PngBitmapEncoder();heldPng.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(heldPreview));
            using(var output=File.Create(Path.Combine(SettingsStore.DataDirectory,"carry-ball-preview.png")))heldPng.Save(output);
            PreparePlay(5);await Task.Delay(700);
            NativeDesktop.GetWindowRect(petHandle,out var restStart);await Task.Delay(400);NativeDesktop.GetWindowRect(petHandle,out var restEnd);
            checks["rest_holds_ball_standing_still"]=toy.PetHeld && app.Pet.CurrentAnimation=="restingball" && Math.Abs(restEnd.Left-restStart.Left)<2;
            PreparePlay(3);await Task.Delay(700);
            checks["toss_pickup_attaches_ball"]=toy.PetHeld;
            await Task.Delay(650);NativeDesktop.GetWindowRect(petHandle,out var throwBody);
            checks["autonomous_toss_flies_up"]=!toy.PetHeld && toy.CenterY<throwBody.Top+throwBody.Height*.6;
            app.Pet.CancelBallPlay();app.Pet.ReturnToCursor();app.Pet.SetMood("idle");await Task.Delay(250);
            void SetPet(string name,object value)=>typeof(PetWindow).GetField(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(app.Pet,value);
            SetPet("interactionUntil",DateTime.MinValue);SetPet("nextHead",DateTime.MinValue);SetPet("nextKick",DateTime.UtcNow.AddSeconds(5));
            NativeDesktop.GetWindowRect(petHandle,out var headBody);
            toy.TakeByPet();toy.FollowHands(headBody.Left+headBody.Width*.5,headBody.Top+headBody.Height*.2-toy.Radius-12);toy.ReleaseFromPet(0,150);
            await Until(()=>app.Pet.CurrentAnimation=="headball");
            checks["descending_ball_heads_on_contact"]=toy.VelocityY<0;
            app.Settings.Mode="explore";app.ApplySettings();
            checks["changing_mode_keeps_ball"]=Application.Current.Windows.OfType<ToyBallWindow>().SingleOrDefault()==toy;
            app.Pet.CancelBallPlay();app.Pet.TryDockWindow(handle);
            var controls=new SettingsWindow(app);controls.Show();controls.Hide();
            checks["toy_does_not_block_activity"]=!app.Pet.InteractionOpen;controls.Close();
            app.Pet.ToggleBall();checks["toy_closed"]=!Application.Current.Windows.OfType<ToyBallWindow>().Any();
            app.Settings.Mode="companion";app.ApplySettings();app.Pet.ReturnToCursor();app.Pet.SetMood("idle");await Task.Delay(900);
            NativeDesktop.GetWindowRect(petHandle,out var stillStart);await Task.Delay(700);NativeDesktop.GetWindowRect(petHandle,out var stillEnd);
            checks["companion_without_ball_stays_put"]=stillStart.Left==stillEnd.Left && stillStart.Top==stillEnd.Top;
            app.Settings.Mode="explore";app.ApplySettings();app.Pet.SetMood("idle");await Task.Delay(1000);
            NativeDesktop.GetWindowRect(petHandle,out var exploreStart);await Task.Delay(600);NativeDesktop.GetWindowRect(petHandle,out var exploreEnd);
            checks["exploration_without_ball_moves"]=Math.Abs(exploreEnd.Left-exploreStart.Left)>5 || exploreEnd.Top!=exploreStart.Top;
            checks["exploration_uses_walk_or_run"]=LocomotionRules.IsWalk(app.Pet.CurrentAnimation)||app.Pet.CurrentAnimation is "left" or "right";
            app.Pet.InteractionOpen=true;await Task.Delay(150);NativeDesktop.GetWindowRect(petHandle,out var interactionStart);await Task.Delay(400);NativeDesktop.GetWindowRect(petHandle,out var interactionEnd);
            checks["user_interaction_holds_exploration"]=interactionStart.Left==interactionEnd.Left;
            app.Pet.InteractionOpen=false;
            app.Pet.ToggleBall();await Task.Delay(200);
            checks["exploration_can_release_ball"]=Application.Current.Windows.OfType<ToyBallWindow>().Any();
            app.Pet.ToggleBall();app.Pet.TryDockWindow(handle);
            object GetPet(string name)=>typeof(PetWindow).GetField(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(app.Pet)!;
            object? InvokePet(string name,params object[] values)=>typeof(PetWindow).GetMethod(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(app.Pet,values);
            app.Settings.Mode="companion";app.ApplySettings();app.Pet.TryDockWindow(handle);
            SetPet("seatUntil",DateTime.UtcNow.AddSeconds(-1));await Task.Delay(250);
            checks["companion_manual_perch_has_no_timeout"]=app.Pet.IsDocked&&(bool)GetPet("perchSeated");
            app.Settings.Mode="explore";app.ApplySettings();app.Pet.TryDockWindow(handle);app.Pet.SetMood("idle");
            double seatRemaining=((DateTime)GetPet("seatUntil")-DateTime.UtcNow).TotalSeconds;
            checks["manual_drop_sits_for_at_most_60_seconds"]=(bool)GetPet("perchSeated")&&seatRemaining>58&&seatRemaining<=60;
            SetPet("interactionUntil",DateTime.MinValue);SetPet("seatUntil",DateTime.UtcNow.AddSeconds(-1));await Task.Delay(150);
            checks["exploration_expired_seat_resumes_or_jumps"]=!app.Pet.IsDocked||!(bool)GetPet("perchSeated");
            var jumpArea=NativeDesktop.WorkAreas().OrderByDescending(r=>r.Width).First();
            await Command($"screen:{jumpArea.Left+400},{jumpArea.Bottom-330}");
            app.Pet.ReturnToCursor();app.Pet.SetMood("idle");
            NativeDesktop.Position(petHandle,jumpArea.Left+200,jumpArea.Bottom-208);await Task.Delay(150);
            NativeDesktop.GetWindowRect(petHandle,out var jumpBody);NativeDesktop.TrySurface(handle,out var jumpWindow);
            double launchX=jumpWindow.Left-jumpBody.Width*.65,launchY=jumpArea.Bottom-jumpBody.Height*203.0/208;
            NativeDesktop.Position(petHandle,launchX,launchY);SetPet("x",launchX);SetPet("y",launchY);
            SetPet("interactionUntil",DateTime.MinValue);SetPet("pettedAt",DateTime.MinValue);
            InvokePet("LaunchWindowJump",DateTime.UtcNow,new NativeDesktop.Surface(handle,jumpWindow),jumpWindow.Left+jumpBody.Width*.55,(double)jumpBody.Width,(double)jumpBody.Height,jumpArea);
            await Until(()=>app.Pet.CurrentAnimation=="jump-up");checks["window_jump_has_ascending_pose"]=!app.Pet.IsDocked;
            await Until(()=>app.Pet.DockedWindow==handle);
            checks["autonomous_jump_lands_standing"]=!(bool)GetPet("perchSeated");
            SetPet("velocityX",40d);SetPet("actionUntil",DateTime.UtcNow.AddSeconds(20));SetPet("platformDecisionAt",DateTime.UtcNow.AddSeconds(20));
            NativeDesktop.GetWindowRect(petHandle,out var platformStart);await Task.Delay(650);NativeDesktop.GetWindowRect(petHandle,out var platformEnd);
            checks["walks_on_window_top"]=app.Pet.IsDocked&&platformEnd.Left>platformStart.Left&&Math.Abs(platformEnd.Top+jumpBody.Height*203.0/208-jumpWindow.Top)<4;
            app.Pet.Paused=true;NativeDesktop.GetWindowRect(petHandle,out var followStart);await Command("move");await Task.Delay(250);NativeDesktop.GetWindowRect(petHandle,out var followEnd);
            checks["standing_platform_tracks_moving_window"]=app.Pet.IsDocked&&Math.Abs(followEnd.Left-followStart.Left-100)<4&&Math.Abs(followEnd.Top-followStart.Top-30)<4;
            app.Pet.Paused=false;InvokePet("LeavePlatform",DateTime.UtcNow,(double)jumpBody.Width);await Task.Delay(150);
            checks["deliberate_jump_down_leaves_platform"]=!app.Pet.IsDocked;
            app.Settings.Mode="companion";app.ApplySettings();app.Pet.ReturnToCursor();app.Pet.SetMood("idle");SetPet("interactionUntil",DateTime.MinValue);
            InvokePet("StartIdleAction",DateTime.UtcNow);NativeDesktop.GetWindowRect(petHandle,out var gestureStart);await Task.Delay(650);NativeDesktop.GetWindowRect(petHandle,out var gestureEnd);
            checks["new_idle_gesture_stays_in_place"]=gestureStart.Left==gestureEnd.Left&&gestureStart.Top==gestureEnd.Top&&new[]{"ear-groom","collar-tidy","hand-warm","yawn","proud-nod"}.Contains(app.Pet.CurrentAnimation);
            app.Pet.Paused=true;
            await Command($"screen:{jumpArea.Left+500},{jumpArea.Bottom-360}");
            app.Pet.TryDockWindow(handle);
            app.Pet.ToggleBall();var windowToy=Application.Current.Windows.OfType<ToyBallWindow>().Single();
            checks["release_ball_preserves_pet_window_contact"]=app.Pet.DockedWindow==handle;
            checks["ball_released_on_window_starts_on_ledge"]=windowToy.Platform==handle;
            NativeDesktop.TrySurface(handle,out var ballWindow);
            void PlaceWindowBall(double cx,double cy,double horizontal=0,double vertical=0)
            {windowToy.TakeByPet();windowToy.FollowHands(cx,cy);windowToy.ReleaseFromPet(horizontal,vertical);}
            void SeedWindowLanding()=>typeof(ToyBallWindow).GetField("platformRandom",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(windowToy,new Random(1));
            PlaceWindowBall(ballWindow.Left+ballWindow.Width*.5,ballWindow.Top-windowToy.Radius-60,50,60);SeedWindowLanding();
            for(int i=0;i<30&&windowToy.Platform==IntPtr.Zero;i++)windowToy.Tick(.04,false);
            checks["descending_ball_can_settle_on_window"]=windowToy.Platform==handle;
            double rollStart=windowToy.CenterX;
            for(int i=0;i<8;i++)windowToy.Tick(.04,false);
            checks["ball_rolls_along_window_top"]=windowToy.Platform==handle&&windowToy.CenterX>rollStart&&Math.Abs(windowToy.Bottom-ballWindow.Top)<3;
            double trackedX=windowToy.CenterX,trackedY=windowToy.CenterY;await Command("move");await Task.Delay(200);
            checks["window_ball_follows_normal_window_movement"]=windowToy.Platform==handle&&Math.Abs(windowToy.CenterX-trackedX-100)<3&&Math.Abs(windowToy.CenterY-trackedY-30)<3;
            NativeDesktop.TrySurface(handle,out ballWindow);
            PlaceWindowBall(ballWindow.Right-windowToy.Radius-1,ballWindow.Top-windowToy.Radius);
            windowToy.AttachToWindow(handle,350);windowToy.Tick(.1,false);windowToy.Tick(.1,false);
            checks["rolling_beyond_window_edge_falls"]=windowToy.Platform==IntPtr.Zero&&windowToy.Bottom>ballWindow.Top;
            windowToy.TakeByPet();windowToy.FollowHands(ballWindow.Left+ballWindow.Width*.5,jumpArea.Bottom-60);
            SeedWindowLanding();bool targeted=windowToy.TossToWindow(new NativeDesktop.Surface(handle,ballWindow),ballWindow.Left+ballWindow.Width*.5,jumpArea);
            checks["ground_toss_targets_window_top"]=targeted&&windowToy.VelocityY<0;
            for(int i=0;i<240&&windowToy.Platform==IntPtr.Zero;i++)windowToy.Tick(.016,false);
            checks["targeted_toss_lands_on_window"]=windowToy.Platform==handle;
            app.Pet.ReturnToCursor();app.Pet.SetMood("idle");NativeDesktop.Position(petHandle,ballWindow.Left+40,jumpArea.Bottom-208);await Task.Delay(200);
            NativeDesktop.GetWindowRect(petHandle,out var chaseBody);
            double chaseX=ballWindow.Left+40,chaseY=jumpArea.Bottom-chaseBody.Height*203.0/208;
            NativeDesktop.Position(petHandle,chaseX,chaseY);SetPet("x",chaseX);SetPet("y",chaseY);SetPet("grounded",true);SetPet("perch",IntPtr.Zero);
            SetPet("interactionUntil",DateTime.MinValue);SetPet("nextKick",DateTime.UtcNow.AddSeconds(30));SetPet("nextBallWindowJump",DateTime.MinValue);
            app.Pet.Paused=false;await Until(()=>app.Pet.CurrentAnimation=="jump-up");
            checks["companion_play_jumps_up_to_window_ball"]=app.Pet.EffectiveMode=="companion";
            await Until(()=>app.Pet.DockedWindow==handle&&!(bool)GetPet("perchSeated"));
            checks["ball_chase_lands_standing_on_window"]=app.Pet.DockedWindow==handle;
            app.Pet.Paused=true;app.Pet.TryDockWindow(handle);SetPet("perchSeated",false);NativeDesktop.GetWindowRect(petHandle,out chaseBody);
            SetPet("y",ballWindow.Top-chaseBody.Height*203.0/208);NativeDesktop.Position(petHandle,chaseBody.Left,ballWindow.Top-chaseBody.Height*203.0/208);
            PlaceWindowBall(chaseBody.Left+chaseBody.Width*.8,ballWindow.Top-windowToy.Radius);windowToy.AttachToWindow(handle,0);
            SetPet("interactionUntil",DateTime.MinValue);SetPet("ballPlayKind",4);SetPet("kickDirection",1d);SetPet("kickStarted",DateTime.UtcNow);SetPet("kickUntil",DateTime.UtcNow.AddSeconds(3));SetPet("kickApplied",false);
            app.Pet.Paused=false;await Task.Delay(700);NativeDesktop.GetWindowRect(petHandle,out var ledgeCarryStart);await Task.Delay(400);NativeDesktop.GetWindowRect(petHandle,out var ledgeCarryEnd);
            checks["can_pick_up_and_carry_ball_on_window"]=windowToy.PetHeld&&app.Pet.DockedWindow==handle&&app.Pet.CurrentAnimation=="carryingball"&&ledgeCarryEnd.Left>ledgeCarryStart.Left;
            windowToy.TossDownFromWindow(handle,1,.5);SetPet("kickUntil",DateTime.MinValue);SetPet("nextBallWindowJump",DateTime.MinValue);
            checks["window_toss_can_launch_downward"]=!windowToy.PetHeld&&windowToy.VelocityY>0;
            await Until(()=>windowToy.Bottom>ballWindow.Top+chaseBody.Height*.3&&app.Pet.DockedWindow!=handle);
            checks["pet_jumps_down_after_ball"]=!app.Pet.IsDocked;
            app.Settings.Mode="explore";app.ApplySettings();app.Pet.Paused=true;app.Pet.TryDockWindow(handle);
            PlaceWindowBall(ballWindow.Left+ballWindow.Width*.5,ballWindow.Top-windowToy.Radius);windowToy.AttachToWindow(handle,0);
            SetPet("interactionUntil",DateTime.MinValue);SetPet("nextKick",DateTime.UtcNow.AddSeconds(10));app.Pet.Paused=false;await Task.Delay(300);
            checks["explore_play_stands_on_window"]=app.Pet.DockedWindow==handle&&!(bool)GetPet("perchSeated");
            app.Pet.Paused=true;PlaceWindowBall(ballWindow.Left+ballWindow.Width*.5,ballWindow.Top-windowToy.Radius);windowToy.AttachToWindow(handle,0);
            await Command("close");await Task.Delay(350);
            checks["released_closed_window"]=app.Pet.DockedWindow==IntPtr.Zero;
            checks["window_ball_releases_closed_window"]=windowToy.Platform==IntPtr.Zero;
            app.Pet.ToggleBall();app.Pet.Paused=false;
            var settingsPanel=new SettingsWindow(app);
            var modeCombo=(ComboBox)typeof(SettingsWindow).GetField("_mode",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(settingsPanel)!;
            checks["settings_has_only_two_modes"]=modeCombo.Items.Count==2;
            settingsPanel.Show();await Task.Delay(300);settingsPanel.UpdateLayout();
            IEnumerable<DependencyObject> Descendants(DependencyObject node)
            {
                foreach(var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
                {yield return child;foreach(var nested in Descendants(child))yield return nested;}
            }
            var settingsControls=Descendants(settingsPanel).ToArray();
            var buttonLabels=settingsControls.OfType<Button>().Select(b=>b.Content as string).ToHashSet();
            checks["settings_removes_redundant_buttons"]=!new[]{"停靠視窗","框選畫面","儲存設定","關閉","返回對話","收起"}.Any(buttonLabels.Contains);
            modeCombo.SelectedIndex=1;
            checks["default_mode_autosaves_while_temporarily_companion"]=app.Settings.Mode=="explore"&&app.Pet.EffectiveMode=="companion"&&SettingsStore.Load().Mode=="explore";
            var scales=settingsControls.OfType<Slider>().ToArray();double previousScale=app.Settings.PetScale;
            scales[0].Value=1.2;checks["slider_does_not_resize_while_adjusting"]=app.Settings.PetScale==previousScale;
            scales[0].RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(0,0,false){RoutedEvent=System.Windows.Controls.Primitives.Thumb.DragCompletedEvent});
            checks["slider_applies_on_release"]=Math.Abs(app.Settings.PetScale-1.2)<.001;
            scales[0].Value=previousScale;scales[0].RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(0,0,false){RoutedEvent=System.Windows.Controls.Primitives.Thumb.DragCompletedEvent});
            var voiceBox=(TextBox)typeof(SettingsWindow).GetField("_voice",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(settingsPanel)!;
            voiceBox.Text="offline-autosave-test";await Task.Delay(750);
            checks["text_setting_autosaves_without_button"]=SettingsStore.Load().VoiceId=="offline-autosave-test";
            settingsPanel.Hide();checks["hidden_settings_restores_default_mode"]=app.Pet.EffectiveMode=="explore";
            settingsPanel.Show();
            // Direct right-button routed event exercises the same navigation handler as the pointer.
            settingsPanel.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount,System.Windows.Input.MouseButton.Right){RoutedEvent=UIElement.PreviewMouseRightButtonUpEvent});
            checks["right_click_settings_returns_to_banner"]=!settingsPanel.IsVisible&&Application.Current.Windows.OfType<ChatWindow>().Any(w=>w.IsVisible)&&app.Pet.EffectiveMode=="companion";
            var banner=Application.Current.Windows.OfType<ChatWindow>().First(w=>w.IsVisible);
            banner.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount,System.Windows.Input.MouseButton.Right){RoutedEvent=UIElement.PreviewMouseRightButtonUpEvent});
            checks["right_click_banner_hides_and_restores_default"]=!banner.IsVisible&&app.Pet.EffectiveMode=="explore";
            var detected=app.Pet.DetectedScreens.ToArray();
            checks["screen_detection_returns_unique_ids"]=detected.Length==NativeDesktop.WorkAreas().Count&&detected.Select(s=>s.Id).Distinct().Count()==detected.Length&&detected.All(s=>!string.IsNullOrWhiteSpace(s.Id));
            app.Pet.Paused=true;
            foreach(var screen in detected)
            {
                app.Settings.AllowedScreenIds=new[]{screen.Id};app.ApplySettings();app.Pet.ReturnToCursor();await Task.Delay(200);
                NativeDesktop.GetWindowRect(petHandle,out var scoped);
                checks[$"recall_obeys_screen_{screen.Id}"]=scoped.Left>=screen.Left&&scoped.Right<=screen.Right&&scoped.Top>=screen.Top;
            }
            if(detected.Length>1)
            {
                var allowed=detected[0];var blocked=detected[1];app.Settings.AllowedScreenIds=new[]{allowed.Id};app.ApplySettings();
                NativeDesktop.Position(petHandle,blocked.Left+100,blocked.Top+200);await Task.Delay(300);NativeDesktop.GetWindowRect(petHandle,out var recovered);
                checks["blocked_screen_relocated_even_when_paused"]=recovered.Left>=allowed.Left&&recovered.Right<=allowed.Right;
                SetPet("x",(double)blocked.Left+100);SetPet("y",(double)blocked.Top+200);InvokePet("ClampDragToAllowedScreen");
                checks["drag_is_clamped_to_selected_screen"]=(double)GetPet("x")>=allowed.Left&&(double)GetPet("x")<allowed.Right;
                app.Pet.ToggleBall();await Task.Delay(100);var scopedBall=Application.Current.Windows.OfType<ToyBallWindow>().Single();
                var ballType=typeof(ToyBallWindow);ballType.GetField("x",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(scopedBall,(double)blocked.Left+100);
                ballType.GetField("y",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(scopedBall,(double)blocked.Top+200);
                scopedBall.Tick(.033,true);checks["ball_stays_inside_activity_scope"]=scopedBall.CenterX>=allowed.Left&&scopedBall.CenterX<=allowed.Right;app.Pet.ToggleBall();
            }
            app.Settings.AllowedScreenIds=null;app.ApplySettings();app.Pet.ReturnToCursor();
            settingsPanel.Show();await Task.Delay(1100);
            var scopePanel=(StackPanel)typeof(SettingsWindow).GetField("_screenChoices",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(settingsPanel)!;
            checks["settings_lists_every_detected_screen"]=scopePanel.Children.OfType<CheckBox>().Count()==detected.Length+1;
            var allScreens=scopePanel.Children.OfType<CheckBox>().First();allScreens.IsChecked=false;
            checks["screen_scope_autosaves"]=SettingsStore.Load().AllowedScreenIds?.Length==detected.Length;
            foreach(var box in scopePanel.Children.OfType<CheckBox>().Skip(1).ToArray())
            {
                // Refresh rebuilds the list after each selection; use the current matching control.
                var current=scopePanel.Children.OfType<CheckBox>().First(c=>Equals(c.Tag,box.Tag));current.IsChecked=false;
            }
            checks["cannot_deselect_final_activity_screen"]=scopePanel.Children.OfType<CheckBox>().Skip(1).Count(c=>c.IsChecked==true)==1;
            app.Settings.AllowedScreenIds=null;app.ApplySettings();await Task.Delay(1100);
            app.Pet.Paused=false;
            settingsPanel.Show();settingsPanel.UpdateLayout();
            var preview=new System.Windows.Media.Imaging.RenderTargetBitmap((int)settingsPanel.ActualWidth,(int)settingsPanel.ActualHeight,96,96,PixelFormats.Pbgra32);
            preview.Render(settingsPanel);var png=new System.Windows.Media.Imaging.PngBitmapEncoder();
            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(preview));
            using(var output=File.Create(Path.Combine(SettingsStore.DataDirectory,"settings-preview.png")))png.Save(output);
            settingsPanel.Close();
            async Task Command(string command)
            {
                WriteSignal(Path.Combine(directory,"fixture-command.txt"),command);
                await Until(()=>File.Exists(Path.Combine(directory,"fixture-ack.txt"))&&ReadSignal(Path.Combine(directory,"fixture-ack.txt"))==command);
            }
        }
        catch(Exception ex){checks["completed_without_error"]=false;WriteSignal(Path.Combine(directory,"error-type.txt"),ex.GetType().Name);}
        finally
        {
            if(fixture!=null&&!fixture.HasExited)
            {
                WriteSignal(Path.Combine(directory,"fixture-command.txt"),"close");
                try{await fixture.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));}catch(TimeoutException){fixture.Kill();}
            }
            fixture?.Dispose();
            WriteSignal(Path.Combine(SettingsStore.DataDirectory,"interaction-check.json"),JsonSerializer.Serialize(new { passed=checks.Count>=11&&checks.Values.All(v=>v),checks },new JsonSerializerOptions { WriteIndented=true }));
            app.ExitApp();
        }
    }
    // Fixture and controller communicate atomically; readers must permit replacement on Windows.
    private static void WriteSignal(string path,string value)
    {
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {File.WriteAllText(temporary,value);File.Move(temporary,path,true);}
        finally {if(File.Exists(temporary))File.Delete(temporary);}
    }
    private static string ReadSignal(string path)
    {
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        using var reader=new StreamReader(stream);return reader.ReadToEnd();
    }
    private static async Task Until(Func<bool> condition)
    {
        var deadline=DateTime.UtcNow.AddSeconds(8);
        while(!condition()){if(DateTime.UtcNow>deadline)throw new TimeoutException();await Task.Delay(80);}
    }
}

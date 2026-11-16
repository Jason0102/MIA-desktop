using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Mia;
using Size = System.Windows.Size;
using Point = System.Windows.Point;

internal static class InteractionChecks
{
    internal static void Run(Action<bool,string> check)
    {
        TestOverlayPlacement(check);
        TestImeInput(check);
        TestInteractionGeometry(check);
        TestInteractionAssets(check);
        TestFrameAlpha(check);
        TestPosePriority(check);
    }

    private static void TestOverlayPlacement(Action<bool,string> check)
    {
        var type=typeof(PetWindow).Assembly.GetType("Mia.FloatingSurface",throwOnError:true)!;
        var method=type.GetMethod("CalculatePlacement",BindingFlags.Static|BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Overlay placement helper missing");
        Rect Place(Rect pet, Size panel, Rect work) => (Rect)method.Invoke(null,new object[]{pet,panel,work})!;
        bool Inside(Rect panel, Rect work) => panel.Left>=work.Left && panel.Top>=work.Top && panel.Right<=work.Right && panel.Bottom<=work.Bottom;

        var normalWork=new Rect(0,0,1920,1040);
        var bottomPet=new Rect(800,832,192,208);
        var banner=Place(bottomPet,new Size(380,56),normalWork);
        check(banner.Bottom<=bottomPet.Top && Inside(banner,normalWork),"capsule prefers above pet and remains inside work area");
        check(Math.Abs((banner.Left+banner.Width/2)-(bottomPet.Left+bottomPet.Width/2))<1,"capsule centers on character when room permits");

        var topPet=new Rect(800,8,192,208);
        var side=Place(topPet,new Size(400,480),normalWork);
        check(Inside(side,normalWork) && !side.IntersectsWith(topPet),"top-edge settings panel uses free space without covering character");

        var negativeWork=new Rect(-2560,-180,2560,1400);
        var mixedDpiPet=new Rect(-300,900,288,312);
        var negative=Place(mixedDpiPet,new Size(570,84),negativeWork);
        check(Inside(negative,negativeWork) && negative.Right<=0,"negative-coordinate monitor with scaled physical dimensions stays on its screen");

        var tinyWork=new Rect(0,0,320,240);
        var oversized=Place(new Rect(100,80,192,160),new Size(600,900),tinyWork);
        check(Inside(oversized,tinyWork) && oversized.Width>0 && oversized.Height>0,"oversized content is bounded to a small work area");

        var disconnected=Place(new Rect(-4000,-3000,192,208),new Size(380,56),normalWork);
        check(Inside(disconnected,normalWork),"panel recovers into available work area after monitor removal");
        var rightEdge=Place(new Rect(1850,600,192,208),new Size(380,56),normalWork);
        check(Inside(rightEdge,normalWork),"partially offscreen pet does not push capsule out of bounds");
    }

    private static void TestImeInput(Action<bool,string> check)
    {
        var method=typeof(ChatWindow).GetMethod("ShouldSubmitEnter",BindingFlags.Static|BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("IME submission guard missing");
        bool Submit(Key key,bool composing,bool commitPending) => (bool)method.Invoke(null,new object[]{key,composing,commitPending})!;
        check(Submit(Key.Enter,false,false),"ordinary Enter submits completed text");
        check(!Submit(Key.Enter,true,false),"Enter selecting a Chinese IME candidate does not submit");
        check(!Submit(Key.Enter,false,true),"IME commit event order cannot accidentally send selected text");
        check(!Submit(Key.ImeProcessed,false,false) && !Submit(Key.Space,false,false),"IME-processed and non-Enter keys never submit");
    }

    private static void TestInteractionGeometry(Action<bool,string> check)
    {
        var type=typeof(PetWindow).Assembly.GetType("Mia.InteractionGeometry",throwOnError:true)!;
        object Invoke(string name,params object[] args) => type.GetMethod(name,BindingFlags.Public|BindingFlags.Static)!.Invoke(null,args)!;
        bool Drag(double dx,double dy,double scale) => (bool)Invoke("HasDragStarted",dx,dy,scale);
        check(!Drag(2,2,1) && Drag(7,0,1),"small left-click movement stays a touch while deliberate movement starts dragging");
        check(!Drag(7,0,2) && Drag(13,0,2),"drag threshold follows monitor DPI rather than physical-pixel jitter");

        bool Snap(double x,double y,double left,double right,double top,double workTop,double anchor,double tolerance) =>
            (bool)Invoke("CanSnap",x,y,left,right,top,workTop,anchor,tolerance);
        check(Snap(350,302,200,600,300,0,203,55),"nearby visible window edge accepts docking contact");
        check(!Snap(350,420,200,600,300,0,203,55),"stale docking candidate outside release tolerance is rejected");
        check(!Snap(100,300,200,600,300,0,203,55),"docking candidate outside horizontal window bounds is rejected");
        check(!Snap(350,100,200,600,100,0,203,55),"window without headroom cannot dock character above screen");

        var clamped=((double X,double Y))Invoke("ClampToWorkArea",-5000d,-3000d,288d,312d,-1920d,-100d,0d,980d);
        check(clamped.X>=-1920 && clamped.Y>=-100 && clamped.X+288<=0 && clamped.Y+312<=980,"physical character bounds recover on negative-coordinate monitor");
        int Frame(double time,int count,double fps) => (int)Invoke("FrameIndex",time,count,fps);
        check(Frame(0,6,6)==0 && Frame(.5,6,6)==3 && Frame(1,6,6)==0,"animation frame index loops predictably at authored frame rate");
        bool Stretch(double idle=90,bool grounded=true,bool docked=false,bool paused=false,string mode="companion",bool open=false,bool speaking=false,bool listening=false,bool moodIdle=true,bool hasAsset=true) =>
            (bool)Invoke("CanStretch",idle,grounded,docked,paused,mode,open,speaking,listening,moodIdle,hasAsset);
        check(!Stretch(idle:89.99) && Stretch(),"stretch begins only after ninety seconds of idle eligibility");
        check(!Stretch(grounded:false) && !Stretch(docked:true),"stretch never interrupts airborne or docked contact");
        check(!Stretch(paused:true) && !Stretch(mode:"focus"),"paused and focus modes suppress idle stretch");
        check(!Stretch(open:true) && !Stretch(speaking:true) && !Stretch(listening:true) && !Stretch(moodIdle:false),"open controls and active conversation suppress idle stretch");
        check(!Stretch(hasAsset:false),"missing stretch artwork does not start a fake new pose");
    }

    private static void TestInteractionAssets(Action<bool,string> check)
    {
        var directory=Path.Combine(AppContext.BaseDirectory,"Assets","Interactions");
        var manifest=Path.Combine(directory,"manifest.json");
        check(File.Exists(manifest),"new interaction pose manifest is packaged with executable");
        using var json=JsonDocument.Parse(File.ReadAllText(manifest));
        var clips=json.RootElement.GetProperty("clips").EnumerateArray().ToArray();
        var names=clips.Select(x=>x.GetProperty("name").GetString()).ToHashSet();
        check(new[]{"perched","carried","falling","landing","petting","listening","speaking","stretching"}.All(names.Contains),"all eight next-stage interaction states have authored assets");
        foreach(var clip in clips)
        {
            string name=clip.GetProperty("name").GetString()!;
            string file=clip.GetProperty("file").GetString()!;
            var path=Path.GetFullPath(Path.Combine(directory,file));
            check(path.StartsWith(Path.GetFullPath(directory)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),name+" asset remains within interaction package");
            int width=clip.GetProperty("frameWidth").GetInt32(),height=clip.GetProperty("frameHeight").GetInt32(),count=clip.GetProperty("count").GetInt32();
            double fps=clip.GetProperty("fps").GetDouble(),anchor=clip.GetProperty("anchorY").GetDouble();
            using var bitmap=new System.Drawing.Bitmap(path);
            check(width==768 && height==832 && count>0 && bitmap.Width==width*count && bitmap.Height==height && fps>0 && anchor>0 && anchor<=208,name+" HD dimensions, timing and logical contact anchor are valid");
            bool allVisible=true;
            for(int frame=0;frame<count;frame++)
            {
                bool visible=false;
                for(int y=0;y<height && !visible;y+=3)
                for(int x=0;x<width && !visible;x+=3)visible=bitmap.GetPixel(frame*width+x,y).A>20;
                allVisible&=visible;
            }
            check(allVisible && bitmap.GetPixel(0,0).A==0,name+" frames contain character pixels on transparent background");
        }
    }

    private static void TestFrameAlpha(Action<bool,string> check)
    {
        byte[] pixels={40,60,80,0,40,60,80,15,40,60,80,16,40,60,80,255};
        var bitmap=System.Windows.Media.Imaging.BitmapSource.Create(2,2,96,96,System.Windows.Media.PixelFormats.Bgra32,null,pixels,8);
        var bank=typeof(PetWindow).Assembly.GetType("Mia.PetAnimationBank",true)!;
        var frame=bank.GetMethod("CreateFrame",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,new object[]{bitmap})!;
        var alpha=(byte[])frame.GetType().GetProperty("Alpha")!.GetValue(frame)!;
        check(alpha.SequenceEqual(new byte[]{0,15,16,255}),"compact hit mask preserves exact alpha across rows and click threshold");
    }

    private static void TestPosePriority(Action<bool,string> check)
    {
        // Instantiate the real visual without creating an HWND or examining the desktop.
        // Physical fixtures model mutually competing inputs and verify the chosen/rendered pose.
        var pet=new PetWindow();
        try
        {
            var type=typeof(PetWindow);
            var loaded=(System.Collections.IDictionary)type.GetField("frames",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(pet)!;
            bool allHd=loaded.Count==73;
            foreach(var frame in loaded.Values)
                allHd &= (int)frame.GetType().GetProperty("Width")!.GetValue(frame)! == 768 && (int)frame.GetType().GetProperty("Height")!.GetValue(frame)! == 832;
            check(allHd,"runtime loads all 73 base frames at approved four-times resolution");
            var select=type.GetMethod("SelectPose",BindingFlags.Instance|BindingFlags.NonPublic)!;
            var pointType=type.Assembly.GetType("Mia.NativeDesktop+Point",true)!;
            object point=Activator.CreateInstance(pointType)!;
            void Set(string name,object value)=>type.GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(pet,value);
            void Select()=>select.Invoke(pet,new object[]{DateTime.UtcNow,point,192d,208d});
            void Baseline()
            {
                Set("dragging",false);Set("dragMoved",false);Set("grounded",true);Set("perch",IntPtr.Zero);
                Set("landingAt",DateTime.MinValue);Set("pettedAt",DateTime.MinValue);Set("velocityX",0d);
                Set("gazeUntil",DateTime.MinValue);pet.InteractionOpen=false;
                Set("stretchUntil",DateTime.MinValue);Set("kickUntil",DateTime.MinValue);Set("headUntil",DateTime.MinValue);
                Set("idleAction","");Set("idleUntil",DateTime.MinValue);Set("perchSeated",true);Set("velocityY",0d);
                pet.SetMode("companion");pet.SetMood("idle");pet.BeginListening(false);pet.SetSpeaking(false);
            }
            Baseline();Set("velocityX",80d);Select();check(pet.CurrentAnimation=="walk-right","real pet selects walking at exploration speed");
            Set("velocityX",-80d);Select();check(pet.CurrentAnimation=="walk-left","real pet turns walking left");
            var facing=(System.Windows.Media.ScaleTransform)type.GetField("kickFacing",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(pet)!;
            check(facing.ScaleX==-1,"left walking mirrors the authored right-facing cycle");
            Set("velocityX",180d);Select();check(pet.CurrentAnimation=="right","real pet switches to running at chase speed");
            check(facing.ScaleX==1,"switching from left walk to right run clears mirroring");
            check(pet.AvailableInteractionAnimations.Contains("walking"),"dedicated walking artwork is loaded");
            pet.SetMode("explore");int modeChanges=0;pet.ModeChanged+=_=>modeChanges++;
            pet.InteractionOpen=true;check(pet.EffectiveMode=="companion"&&modeChanges==0,"opening controls temporarily selects companion without persisting a new default");
            pet.InteractionOpen=false;check(pet.EffectiveMode=="explore"&&modeChanges==0,"closing controls restores configured exploration");
            pet.InteractionOpen=true;pet.SetMode("companion");pet.InteractionOpen=false;
            check(pet.EffectiveMode=="companion"&&modeChanges==1,"changing the default while controls are open is honored on dismissal");
            check(new[]{"ear-groom","collar-tidy","hand-warm","yawn","proud-nod","jump-up"}.All(pet.AvailableInteractionAnimations.Contains),"five new idle gestures and jumping artwork load at runtime");
            Baseline();pet.SetMode("explore");Set("interactionUntil",DateTime.MinValue);Set("x",-96d);Set("y",-93.6d);Set("velocityX",100d);
            var attention=(CursorAttention)type.GetField("cursorAttention",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(pet)!;
            attention.Update(DateTime.UtcNow.AddSeconds(20),true,0,()=>0);
            var updateAttention=type.GetMethod("UpdateCursorAttention",BindingFlags.Instance|BindingFlags.NonPublic)!;
            updateAttention.Invoke(pet,new object[]{DateTime.UtcNow,point,192d,208d});Select();
            check(pet.CurrentAnimation=="gaze"&&(double)type.GetField("velocityX",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(pet)! ==0,"real pet stops movement and selects directional gaze when attracted");
            pointType.GetField("X")!.SetValue(point,2000);
            updateAttention.Invoke(pet,new object[]{DateTime.UtcNow,point,192d,208d});
            check(!attention.Active&&(DateTime)type.GetField("actionUntil",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(pet)! ==DateTime.MinValue,"cursor departure releases gaze and requests immediate exploration movement");
            type.GetMethod("ChooseExplorationMotion",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(pet,new object[]{DateTime.UtcNow,192d,false});Select();
            check(LocomotionRules.IsWalk(pet.CurrentAnimation)||pet.CurrentAnimation is "left" or "right","exploration resumes walking or running after cursor departure");
            pointType.GetField("X")!.SetValue(point,0);
            Baseline();Set("perch",new IntPtr(1));Set("perchSeated",false);Set("velocityX",80d);Select();
            check(pet.CurrentAnimation=="walk-right","standing on a window uses walking instead of seated artwork");
            var touch=type.GetMethod("TouchCharacter",BindingFlags.Instance|BindingFlags.NonPublic)!;
            var petHead=type.GetMethod("PetHead",BindingFlags.Instance|BindingFlags.NonPublic)!;
            var expression=type.GetMethod("UpdateExpression",BindingFlags.Instance|BindingFlags.NonPublic)!;
            Baseline();Set("perch",new IntPtr(1));Select();
            var seatedFrame=type.GetField("currentFrame",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(pet);
            petHead.Invoke(pet,null);Select();
            check(pet.CurrentAnimation=="perched-petting","stroking the head while seated starts the seated touch animation");
            check(ReferenceEquals(seatedFrame,type.GetField("currentFrame",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(pet)),"seated touch retains seated artwork instead of standing up");
            foreach(double scale in new[]{.5,1,3})
            {
                pet.Height=208*scale;pet.Width=192*scale;
                Set("pettedAt",DateTime.MinValue);touch.Invoke(pet,new object[]{new Point(96*scale,60*scale)});Select();
                check(pet.CurrentAnimation=="perched-petting","seated head click reacts at scale "+scale);
                Set("pettedAt",DateTime.MinValue);touch.Invoke(pet,new object[]{new Point(96*scale,160*scale)});Select();
                check(pet.CurrentAnimation=="perched-petting","seated body click reacts at scale "+scale);
            }
            pet.Height=208;pet.Width=192;
            var touchStart=(DateTime)type.GetField("pettedAt",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(pet)!;
            var reactionTime=touchStart.AddSeconds(.375);
            expression.Invoke(pet,new object[]{reactionTime,1d});
            var reactionSprite=(System.Windows.Controls.Image)type.GetField("sprite",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(pet)!;
            var reactionTilt=(System.Windows.Media.RotateTransform)type.GetField("expressionTilt",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(pet)!;
            var reactionScale=(System.Windows.Media.ScaleTransform)type.GetField("expressionScale",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(pet)!;
            check(Math.Abs(reactionTilt.Angle)>2&&reactionScale.ScaleY<1,"seated touch visibly sways and nods during playback");
            check(Math.Abs(reactionSprite.RenderTransformOrigin.Y-145d/208)<.0001&&pet.IsDocked,"touch pivots at the seat and retains docking");
            check((double)type.GetMethod("PerchAnchor",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(pet,new object[]{208d})! ==145,"seated touch keeps the same physical window contact height");
            select.Invoke(pet,new object[]{touchStart.AddSeconds(1.5),point,192d,208d});
            check(pet.CurrentAnimation=="perched"&&pet.IsDocked,"touch ends after 1.5 seconds and restores the seated idle");
            petHead.Invoke(pet,null);pet.SetSpeaking(true);Select();
            check(pet.CurrentAnimation=="speaking","speech still overrides seated touch");
            pet.SetSpeaking(false);Set("grounded",false);Select();
            check(pet.CurrentAnimation=="falling","loss of support overrides seated touch");
            Set("dragging",true);Set("dragMoved",true);Select();
            check(pet.CurrentAnimation=="carried","dragging overrides seated touch");
            Baseline();touch.Invoke(pet,new object[]{new Point(96,60)});Select();
            check(pet.CurrentAnimation=="petting","standing head click retains its existing touch animation");
            Baseline();touch.Invoke(pet,new object[]{new Point(96,160)});Select();
            check(pet.CurrentAnimation=="happy","standing body click retains its happy response");
            Baseline();Set("perch",new IntPtr(1));Select();check(pet.CurrentAnimation=="perched","manual seated contact retains sitting pose");
            Baseline();Set("grounded",false);Set("velocityY",-300d);Select();check(pet.CurrentAnimation=="jump-up","ascending hop has dedicated jump pose");
            Set("velocityY",100d);Select();check(pet.CurrentAnimation=="falling","jump descent transitions to falling artwork");
            Baseline();Set("idleAction","yawn");Set("idleStarted",DateTime.UtcNow.AddSeconds(-1));Set("idleUntil",DateTime.UtcNow.AddSeconds(3));Select();
            check(pet.CurrentAnimation=="yawn","quiet standing period uses selected idle gesture");
            pet.BeginListening(true);Select();check(pet.CurrentAnimation=="listening","opening interaction cancels autonomous idle gesture");
            Baseline();Set("kickStarted",DateTime.UtcNow.AddSeconds(-.2));Set("kickUntil",DateTime.UtcNow.AddSeconds(.3));Select();
            check(pet.CurrentAnimation=="kicking","kick uses dedicated pose instead of happy sign");
            Set("ballPlayKind",1);Select();check(pet.CurrentAnimation=="tapping","gentle paw play selects its own animation sequence");
            Set("ballPlayKind",2);Select();check(pet.CurrentAnimation=="pouncing","crouch and pounce selects its own animation sequence");
            Set("ballPlayKind",3);Select();check(pet.CurrentAnimation=="tossing","two-handed toss chooses pickup/hold/throw animation");
            Set("ballPlayKind",4);Select();check(pet.CurrentAnimation=="carryingball","carrying ball chooses dedicated walking animation");
            Set("ballPlayKind",5);Select();check(pet.CurrentAnimation=="restingball","post-play rest uses front-facing held-ball breathing pose");
            Set("ballPlayKind",0);
            Set("perch",new IntPtr(1));Set("perchSeated",false);Set("ballPlayKind",3);Select();
            check(pet.CurrentAnimation=="tossing","standing window play retains the throwing animation");
            Set("ballPlayKind",4);Select();check(pet.CurrentAnimation=="carryingball","standing window play supports carrying the ball");
            Set("perch",IntPtr.Zero);Set("perchSeated",true);Set("ballPlayKind",0);
            Set("grounded",false);Select();check(pet.CurrentAnimation=="falling","falling overrides kick");
            Baseline();Set("headUntil",DateTime.UtcNow.AddSeconds(1));Select();check(pet.CurrentAnimation=="headball","physical head contact uses dedicated heading pose");
            Baseline();pet.BeginListening(true);Select();
            check(pet.CurrentAnimation=="listening","open conversation selects listening pose");
            pet.SetMood("thinking");Select();
            check(pet.CurrentAnimation=="thinking","thinking takes priority over passive listening");
            pet.SetSpeaking(true);Select();
            check(pet.CurrentAnimation=="speaking","active speech takes priority over thinking and listening");
            pet.SetSpeaking(false);Select();check(pet.CurrentAnimation=="listening","sentence gaps do not return to thinking or completion pose");
            pet.SetSpeaking(false);pet.SetMood("idle");Select();
            check(pet.CurrentAnimation=="listening","speech completion returns to listening while panel remains open");
            pet.SetSpeaking(true);Set("pettedAt",DateTime.UtcNow);Select();
            check(pet.CurrentAnimation=="speaking","all speech retains EMO animation during touch interaction");
            Set("landingAt",DateTime.UtcNow);Select();
            check(pet.CurrentAnimation=="speaking","grounded speech retains EMO animation after landing");
            Set("perch",new IntPtr(1));Select();
            check(pet.CurrentAnimation=="speaking","untagged speech also uses an EMO animation while docked");
            pet.SetSpeechAnimation(SpeechAnimation.Resolve("[audio: excited]太好了！"));
            Select();
            check(pet.CurrentAnimation=="speaking","tagged speech overrides the seated pose");
            var speechAnchor=(double)type.GetMethod("PerchAnchor",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(pet,new object[]{208d})!;
            check(speechAnchor==203,"emotion speech feet remain on the window edge");
            Set("pettedAt",DateTime.MinValue);
            pet.SetSpeaking(false);Select();
            check(pet.CurrentAnimation=="perched","speech stop restores seated pose");
            Set("grounded",false);Select();
            check(pet.CurrentAnimation=="falling","falling takes priority over seated and social poses");
            Set("dragging",true);Set("dragMoved",true);Select();
            check(pet.CurrentAnimation=="carried","actual drag takes highest pose priority");
            Baseline();pet.SetMode("companion");pet.SetTaskProcessing(true);
            Set("moodUntil",DateTime.UtcNow.AddMinutes(-5));Select();
            check(pet.CurrentAnimation=="thinking","active jobs keep ACT-008 beyond the mood timeout");
            pet.SetSpeaking(true);Select();
            check(pet.CurrentAnimation=="speaking","speech overrides active job ACT-008");
            check(((SpeechAnimation)type.GetField("speechAnimation",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(pet)!).Tag=="peaceful","untagged speech selects authored EMO-028");
            pet.SetSpeaking(false);Select();
            check(pet.CurrentAnimation=="thinking","speech end restores ACT-008 while jobs remain");
            var textId=Guid.NewGuid();
            var textReply=type.GetMethod("SetTextReply",BindingFlags.Instance|BindingFlags.NonPublic)!;
            textReply.Invoke(pet,new object?[]{textId,"[audio: curious]純文字回應"});Select();
            check(pet.CurrentAnimation=="speaking","text-only response uses an EMO animation");
            pet.SetTaskProcessing(false);Select();
            check(pet.CurrentAnimation=="speaking","last task finishing does not interrupt text speech");
            textReply.Invoke(pet,new object?[]{textId,null});Select();
            check(pet.CurrentAnimation!="speaking"&&pet.CurrentAnimation!="thinking","text completion releases EMO and finished task animation");

            Baseline();Set("dragging",true);Select();
            check(pet.CurrentAnimation=="idle","left-button press without movement never selects carrying pose");

            Baseline();pet.SetMode("companion");Set("stretchUntil",DateTime.UtcNow.AddSeconds(2));Select();
            check(pet.CurrentAnimation=="stretching","eligible idle animation selects authored stretch pose");
            Set("stretchUntil",DateTime.UtcNow.AddSeconds(-1));Select();
            check(pet.CurrentAnimation=="idle","stretch ends when its two-second window expires");
            Set("stretchUntil",DateTime.UtcNow.AddSeconds(2));pet.BeginListening(true);Select();
            check(pet.CurrentAnimation=="listening","opening conversation interrupts an ongoing idle stretch");
            pet.BeginListening(false);Set("stretchUntil",DateTime.UtcNow.AddSeconds(2));pet.SetSpeaking(true);Select();
            check(pet.CurrentAnimation=="speaking","speech interrupts an ongoing idle stretch");
            pet.SetSpeaking(false);Set("stretchUntil",DateTime.UtcNow.AddSeconds(2));Set("pettedAt",DateTime.UtcNow);Select();
            check(pet.CurrentAnimation=="petting","touch takes priority over stretch");
            Set("dragging",true);Set("dragMoved",true);Select();
            check(pet.CurrentAnimation=="carried","drag takes priority over stretch and touch");

            // Produce a local QA gallery from the actual runtime Image sources, not from
            // a desktop screenshot. Each tile also verifies decode/render into nonempty pixels.
            string directory=Path.Combine(AppContext.BaseDirectory,"test-data","pose-gallery");
            Directory.CreateDirectory(directory);
            foreach(string name in new[]{"perched","carried","falling","landing","petting","listening","speaking","stretching","kicking","jump-up","ear-groom","collar-tidy","hand-warm","yawn","proud-nod"})
            {
                Baseline();
                switch(name)
                {
                    case "kicking":Set("kickStarted",DateTime.UtcNow.AddSeconds(-.2));Set("kickUntil",DateTime.UtcNow.AddSeconds(.3));break;
                    case "perched":Set("perch",new IntPtr(1));break;
                    case "carried":Set("dragging",true);Set("dragMoved",true);break;
                    case "falling":Set("grounded",false);break;
                    case "jump-up":Set("grounded",false);Set("velocityY",-300d);break;
                    case "ear-groom":case "collar-tidy":case "hand-warm":case "yawn":case "proud-nod":
                        Set("idleAction",name);Set("idleStarted",DateTime.UtcNow.AddSeconds(-2));Set("idleUntil",DateTime.UtcNow.AddSeconds(2));break;
                    case "landing":Set("landingAt",DateTime.UtcNow);break;
                    case "petting":Set("pettedAt",DateTime.UtcNow);break;
                    case "listening":pet.BeginListening(true);break;
                    case "speaking":pet.SetSpeaking(true);break;
                    case "stretching":pet.SetMode("companion");Set("stretchUntil",DateTime.UtcNow.AddSeconds(2));break;
                }
                // Speaking alternates whole poses. Starting offset chooses the authored open-mouth frame.
                if(name=="speaking")
                {
                    type.GetProperty("CurrentAnimation")!.SetValue(pet,name);
                    Set("animationStarted",DateTime.UtcNow.AddSeconds(-.25));
                }
                Select();
                var sprite=(System.Windows.Controls.Image)type.GetField("sprite",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(pet)!;
                sprite.Measure(new Size(192,208));sprite.Arrange(new Rect(0,0,192,208));sprite.UpdateLayout();
                var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(192,208,96,96,System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(sprite);
                var pixels=new byte[192*208*4];bitmap.CopyPixels(pixels,192*4,0);
                check(pixels.Where((_,i)=>i%4==3).Any(a=>a>20),name+" runtime pose renders nonempty transparent artwork");
                var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var stream=File.Create(Path.Combine(directory,name+".png"));encoder.Save(stream);
            }
            Console.WriteLine("Pose QA renders: "+directory);
            foreach (var emotion in SpeechAnimation.All)
            {
                Baseline();
                pet.SetSpeechAnimation(emotion);
                pet.SetSpeaking(true);
                Select();
                check(pet.AvailableInteractionAnimations.Contains("speech-" + emotion.Tag), "authored emotion loaded: " + emotion.Tag);
                var bank = type.GetField("animations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pet)!;
                var clip = bank.GetType().GetMethod("Get", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(bank, new object[] { "speech-" + emotion.Tag })!;
                var authored = (Array)clip.GetType().GetProperty("Frames")!.GetValue(clip)!;
                var current = type.GetField("currentFrame", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pet)!;
                check(authored.Cast<object>().Contains(current), "speaking selects emotion artwork: " + emotion.Tag);
                var image = (System.Windows.Media.Imaging.BitmapSource)current.GetType().GetProperty("Image")!.GetValue(current)!;
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                using var stream = File.Create(Path.Combine(directory, "speech-" + emotion.Tag + ".png"));
                encoder.Save(stream);
                pet.SetSpeaking(false);
                check((SpeechAnimation)type.GetField("speechAnimation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pet)! == SpeechAnimation.Default, "stopping speech resets emotion");
            }
        }
        finally { pet.Stop();pet.Close(); }
    }
}

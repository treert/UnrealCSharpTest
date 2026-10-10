using Script.CoreUObject;
using Script.Engine;

namespace Script.Game.MyTest
{
    [Override]
    public partial class CSharpTest_C
    {
        public CSharpTest_C()
        {
        }

        [Override]
        public override void ReceiveBeginPlay()
        {
            base.ReceiveBeginPlay();
            UKismetSystemLibrary.PrintString(this,"Hello My World");
        }

        [Override]
        public override void ReceiveEndPlay(EEndPlayReason EndPlayReason)
        {
            base.ReceiveEndPlay(EndPlayReason);
        }
    }
}
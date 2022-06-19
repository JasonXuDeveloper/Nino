/* this is generated by nino */
namespace Nino.Test
{
    public partial class NestedData2
    {
        #region NINO_CODEGEN
        public void NinoWriteMembers(Nino.Serialization.Writer writer)
        {
            writer.Write(this.name);
            if(this.ps != null)
            {
                writer.CompressAndWrite(this.ps.Length);
                foreach (var entry in this.ps)
                {
                     entry.NinoWriteMembers(writer);
                }
            }
            else
            {
                writer.CompressAndWrite(0);
            }
            writer.Write(this.vs);
        }

        public void NinoSetMembers(object[] data)
        {
            this.name = (System.String)data[0];
            this.ps = (Nino.Test.Data[])data[1];
            this.vs = (System.Collections.Generic.List<System.Int32>)data[2];
        }
        #endregion
    }
}
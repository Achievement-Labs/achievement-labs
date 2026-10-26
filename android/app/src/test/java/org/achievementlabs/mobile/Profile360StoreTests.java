package org.achievementlabs.mobile;
import org.junit.Test;import static org.junit.Assert.*;import java.nio.file.*;import java.io.*;import java.util.*;
public class Profile360StoreTests {
 interface Action {void run()throws Exception;}
 void rejects(Action action)throws Exception{try{action.run();fail("Invalid save accepted");}catch(IOException expected){}}
 @Test public void atomicWorkingCopyAndBackups()throws Exception{
  Path root=Files.createTempDirectory("labs360-synthetic-");Profile360Store store=new Profile360Store(root.toFile());byte[] original=new byte[40000],edited=original.clone();edited[12]=1;store.load(original);
  rejects(()->store.begin(edited.length,Profile360Store.hash(edited),"changed"));
  store.begin(edited.length,Profile360Store.hash(edited),Profile360Store.hash(original));rejects(()->store.append(1,new byte[1]));rejects(store::commit);assertArrayEquals(original,store.working);store.abort();
  store.begin(edited.length,Profile360Store.hash(edited),Profile360Store.hash(original));store.append(0,Arrays.copyOfRange(edited,0,32768));store.append(32768,Arrays.copyOfRange(edited,32768,edited.length));store.commit();assertArrayEquals(original,store.export(true));assertArrayEquals(edited,store.export(false));assertTrue(Arrays.stream(store.directory.listFiles()).anyMatch(f->f.getName().startsWith("before-save-")));
  Files.write(new File(store.directory,"working.con").toPath(),original);rejects(()->store.export(false));rejects(()->store.begin(edited.length,Profile360Store.hash(edited),Profile360Store.hash(edited)));assertArrayEquals(original,store.original);
 }
}
